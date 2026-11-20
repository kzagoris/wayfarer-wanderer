package wayfarer.gradle;

/**
 * The model the init script's builder returns for one Gradle project: Wayfarer's build target as JSON, so the host and
 * the build under evaluation share nothing but this interface.
 */
public interface WayfarerModel {
    String getJson();
}
