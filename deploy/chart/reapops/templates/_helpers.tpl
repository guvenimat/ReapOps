{{- define "reapops.labels" -}}
app.kubernetes.io/name: {{ . }}
app.kubernetes.io/part-of: reapops
{{- end }}

{{- define "reapops.selector" -}}
app.kubernetes.io/name: {{ . }}
{{- end }}

{{- define "reapops.env" -}}
- name: POSTGRES_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ .Values.secretName }}
      key: POSTGRES_PASSWORD
- name: ConnectionStrings__Postgres
  value: Host=postgres;Database=reapops;Username=reapops;Password=$(POSTGRES_PASSWORD)
{{- end }}

{{- define "reapops.securityContext" -}}
securityContext:
  allowPrivilegeEscalation: false
  readOnlyRootFilesystem: false
  capabilities:
    drop: ["ALL"]
{{- end }}
