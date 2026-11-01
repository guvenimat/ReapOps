CREATE TABLE diagnoses (
    id uuid PRIMARY KEY,
    incident_id uuid NOT NULL REFERENCES incidents (id) ON DELETE CASCADE,
    status text NOT NULL CHECK (status IN ('completed', 'escalated', 'failed')),
    mode text NOT NULL CHECK (mode IN ('off', 'shadow', 'visible')),
    model text NOT NULL,
    prompt_version text NOT NULL,
    summary text NOT NULL,
    root_cause text NOT NULL,
    confidence text NOT NULL CHECK (confidence IN ('low', 'medium', 'high')),
    body jsonb NOT NULL,
    tool_calls int NOT NULL,
    input_tokens int NOT NULL,
    output_tokens int NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX diagnoses_incident_idx ON diagnoses (incident_id, created_at DESC);
