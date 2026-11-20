package wayfarer.gradle;

import java.io.Serializable;

/** The model as the build sends it to the host. */
public final class DefaultWayfarerModel implements WayfarerModel, Serializable {
    private static final long serialVersionUID = 1L;

    private final String json;

    public DefaultWayfarerModel(String json) {
        this.json = json;
    }

    @Override
    public String getJson() {
        return json;
    }
}
