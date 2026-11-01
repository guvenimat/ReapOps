CREATE TABLE incidents (
    id uuid PRIMARY KEY,
    fingerprint text NOT NULL,
    service text NOT NULL,
    kind text NOT NULL,
    title text NOT NULL,
    severity text NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    status text NOT NULL CHECK (status IN ('open', 'acknowledged', 'resolved')),
    opened_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    cleared_at timestamptz,
    acknowledged_at timestamptz,
    acknowledged_by text,
    resolved_at timestamptz,
    resolution text,
    evidence jsonb NOT NULL DEFAULT '[]',
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX incidents_active_fingerprint_idx ON incidents (fingerprint) WHERE status <> 'resolved';
CREATE INDEX incidents_opened_idx ON incidents (opened_at DESC);

CREATE TABLE incident_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_id uuid NOT NULL REFERENCES incidents (id) ON DELETE CASCADE,
    kind text NOT NULL,
    detail text NOT NULL,
    at timestamptz NOT NULL
);

CREATE INDEX incident_events_incident_idx ON incident_events (incident_id, at);

CREATE TABLE notifications (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_id uuid NOT NULL REFERENCES incidents (id) ON DELETE CASCADE,
    kind text NOT NULL,
    decision text NOT NULL,
    at timestamptz NOT NULL
);

CREATE INDEX notifications_at_idx ON notifications (at) WHERE decision = 'send';

CREATE TABLE maintenance_windows (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    service text NOT NULL,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL CHECK (ends_at > starts_at),
    reason text NOT NULL,
    created_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
