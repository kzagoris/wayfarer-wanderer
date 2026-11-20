package wayfarer.maven;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import java.io.File;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import java.util.function.Function;
import org.apache.maven.DefaultMaven;
import org.apache.maven.Maven;
import org.apache.maven.execution.DefaultMavenExecutionRequest;
import org.apache.maven.execution.DefaultMavenExecutionResult;
import org.apache.maven.execution.MavenExecutionRequest;
import org.apache.maven.execution.MavenExecutionRequestPopulator;
import org.apache.maven.execution.MavenSession;
import org.apache.maven.plugin.LegacySupport;
import org.apache.maven.project.DefaultDependencyResolutionRequest;
import org.apache.maven.project.DependencyResolutionException;
import org.apache.maven.project.DependencyResolutionResult;
import org.apache.maven.project.MavenProject;
import org.apache.maven.project.ProjectBuilder;
import org.apache.maven.project.ProjectBuildingException;
import org.apache.maven.project.ProjectBuildingRequest;
import org.apache.maven.project.ProjectBuildingResult;
import org.apache.maven.project.ProjectDependenciesResolver;
import org.apache.maven.settings.Settings;
import org.apache.maven.settings.building.DefaultSettingsBuildingRequest;
import org.apache.maven.settings.building.SettingsBuilder;
import org.codehaus.plexus.ContainerConfiguration;
import org.codehaus.plexus.DefaultContainerConfiguration;
import org.codehaus.plexus.DefaultPlexusContainer;
import org.codehaus.plexus.PlexusConstants;
import org.codehaus.plexus.classworlds.ClassWorld;
import org.eclipse.aether.DefaultRepositorySystemSession;

/**
 * Evaluates a Maven reactor with the Maven whose jars loaded this class: its effective models with profiles, settings
 * and {@code .mvn/maven.config} applied, then each module's dependencies resolved, siblings to their output folders.
 * The host calls {@link #apply} with {@code [requestJson, Consumer<String> partial, BooleanSupplier cancelled]}; each
 * module goes to {@code partial} as a build target's JSON as soon as it is resolved, and the answer is the rest of the
 * workspace's model. Nothing but JDK types crosses between the host's class loader and this one.
 */
public final class MavenModel implements Function<Object[], String> {
    static final Gson JSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();

    private static DefaultPlexusContainer container;

    @Override
    @SuppressWarnings("unchecked")
    public String apply(Object[] arguments) {
        Request request = JSON.fromJson((String) arguments[0], Request.class);
        Consumer<String> partial = (Consumer<String>) arguments[1];
        BooleanSupplier cancelled = (BooleanSupplier) arguments[2];
        Thread thread = Thread.currentThread();
        ClassLoader before = thread.getContextClassLoader();
        thread.setContextClassLoader(MavenModel.class.getClassLoader());
        try {
            return JSON.toJson(evaluate(request, target -> partial.accept(JSON.toJson(target)), cancelled));
        } catch (RuntimeException failure) {
            throw failure;
        } catch (Exception failure) {
            throw new IllegalStateException(failure.getMessage(), failure);
        } finally {
            thread.setContextClassLoader(before);
        }
    }

    private static synchronized DefaultPlexusContainer container() throws Exception {
        if (container == null) {
            ClassWorld world = new ClassWorld("plexus.core", MavenModel.class.getClassLoader());
            ContainerConfiguration configuration = new DefaultContainerConfiguration()
                    .setClassWorld(world)
                    .setRealm(world.getRealm("plexus.core"))
                    .setClassPathScanning(PlexusConstants.SCANNING_INDEX)
                    .setAutoWiring(true)
                    .setJSR250Lifecycle(true)
                    .setName("maven");
            container = new DefaultPlexusContainer(configuration);
        }
        return container;
    }

    // The 3.9 API Maven's own CLI and embedders use; its replacements arrive with Maven 4.
    @SuppressWarnings("deprecation")
    private static Answer evaluate(Request request, Consumer<Targets.Target> partial, BooleanSupplier cancelled) throws Exception {
        DefaultPlexusContainer plexus = container();
        File root = new File(request.root);
        File pom = new File(root, "pom.xml");
        Properties system = new Properties();
        system.putAll(System.getProperties());
        // Profile activation by JDK, and ${java.version}, see the JDK Maven runs on, not the host's.
        if (request.javaVersion != null) {
            system.setProperty("java.version", request.javaVersion);
        }
        if (request.javaHome != null) {
            system.setProperty("java.home", request.javaHome);
        }
        request.environment.forEach((name, value) -> system.setProperty("env." + name, value));
        Properties user = new Properties();
        user.putAll(request.userProperties);
        system.putAll(request.userProperties);

        File userSettings = request.userSettings != null ? new File(request.userSettings) : null;
        File globalSettings = request.globalSettings != null ? new File(request.globalSettings) : null;
        DefaultSettingsBuildingRequest settingsRequest = new DefaultSettingsBuildingRequest();
        settingsRequest.setUserSettingsFile(userSettings);
        settingsRequest.setGlobalSettingsFile(globalSettings);
        settingsRequest.setSystemProperties(system);
        settingsRequest.setUserProperties(user);
        Settings settings = plexus.lookup(SettingsBuilder.class).build(settingsRequest).getEffectiveSettings();

        MavenExecutionRequest execution = new DefaultMavenExecutionRequest();
        execution.setBaseDirectory(root);
        execution.setMultiModuleProjectDirectory(root);
        execution.setPom(pom);
        execution.setSystemProperties(system);
        execution.setUserProperties(user);
        execution.setUserSettingsFile(userSettings);
        execution.setGlobalSettingsFile(globalSettings);
        execution.setActiveProfiles(new ArrayList<>(request.activeProfiles));
        execution.setInactiveProfiles(new ArrayList<>(request.inactiveProfiles));
        if (request.localRepository != null) {
            execution.setLocalRepositoryPath(new File(request.localRepository));
        }
        execution.setOffline(request.offline);
        MavenExecutionRequestPopulator populator = plexus.lookup(MavenExecutionRequestPopulator.class);
        populator.populateFromSettings(execution, settings);
        populator.populateDefaults(execution);

        DefaultMaven maven = (DefaultMaven) plexus.lookup(Maven.class);
        DefaultRepositorySystemSession session = (DefaultRepositorySystemSession) maven.newRepositorySession(execution);
        MavenSession mavenSession = new MavenSession(plexus, session, execution, new DefaultMavenExecutionResult());
        LegacySupport legacy = plexus.lookup(LegacySupport.class);
        MavenSession previous = legacy.getSession();
        legacy.setSession(mavenSession);
        try {
            ProjectBuildingRequest building = execution.getProjectBuildingRequest();
            building.setRepositorySession(session);
            building.setProcessPlugins(true);
            building.setResolveDependencies(false);
            List<ProjectBuildingResult> results;
            try {
                results = plexus.lookup(ProjectBuilder.class).build(List.of(pom), true, building);
            } catch (ProjectBuildingException failure) {
                throw new IllegalStateException(Problems.of(failure), failure);
            }
            List<MavenProject> projects = new ArrayList<>();
            for (ProjectBuildingResult result : results) {
                projects.add(result.getProject());
            }
            mavenSession.setProjects(projects);
            session.setWorkspaceReader(new Reactor(projects));

            ProjectDependenciesResolver resolver = plexus.lookup(ProjectDependenciesResolver.class);
            Targets targets = new Targets(root.toPath(), projects, request);
            for (MavenProject project : projects) {
                if (cancelled.getAsBoolean()) {
                    throw new IllegalStateException("cancelled");
                }
                DependencyResolutionResult resolved;
                String problem = null;
                try {
                    resolved = resolver.resolve(new DefaultDependencyResolutionRequest(project, session));
                } catch (DependencyResolutionException failure) {
                    resolved = failure.getResult();
                    problem = Problems.of(failure);
                }
                partial.accept(targets.of(project, resolved, problem));
            }
            Answer answer = new Answer();
            answer.buildFile = pom.getPath();
            answer.buildInputs = targets.buildInputs(root, userSettings, globalSettings);
            return answer;
        } finally {
            legacy.setSession(previous);
        }
    }

    /** What the host asks: the workspace, the JDK Maven runs on, and what its command line would have said. */
    static final class Request {
        String root;
        String javaHome;
        String javaVersion;
        String localRepository;
        String userSettings;
        String globalSettings;
        boolean offline;
        Map<String, String> userProperties = new LinkedHashMap<>();
        Map<String, String> environment = new LinkedHashMap<>();
        List<String> activeProfiles = new ArrayList<>();
        List<String> inactiveProfiles = new ArrayList<>();
    }

    /** The rest of the workspace's model: its build file and the files the evaluation read. */
    static final class Answer {
        String buildFile;
        List<String> buildInputs;
    }

    static String relative(Path root, Path path) {
        String relative = root.relativize(path).toString().replace(File.separatorChar, '/');
        return relative.isEmpty() ? "." : relative;
    }
}
