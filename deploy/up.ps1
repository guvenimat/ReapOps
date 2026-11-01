param(
    [string]$Cluster = "reapseek",
    [string]$Revision = "main",
    [switch]$SkipBuild,
    [switch]$Local
)

$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine")
foreach ($tool in "docker", "k3d", "kubectl", "helm") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not installed" }
}

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $PSScriptRoot ".env"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Run([scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $block" }
}
function Secret { -join ((1..24) | ForEach-Object { "abcdefghijkmnpqrstuvwxyz23456789"[(Get-Random -Maximum 32)] }) }

if (-not (Test-Path $envFile)) {
    Step "Creating deploy/.env with random secrets"
    @(
        "POSTGRES_PASSWORD=$(Secret)",
        "CONSOLE_PASSWORD=$(Secret)",
        "CONSOLE_SIGNING_KEY=$(Secret)$(Secret)"
    ) | Set-Content -Path $envFile -Encoding ascii
}

Run { kubectl config use-context "k3d-$Cluster" | Out-Null }

if (-not $SkipBuild) {
    Step "Building images"
    Run { docker build -t reapops-api:local -f (Join-Path $root "Dockerfile.api") $root }
    Run { docker build -t reapops-worker:local -f (Join-Path $root "src\ReapOps.Worker\Dockerfile") (Join-Path $root "src") }
}
Step "Importing images into the cluster"
Run { k3d image import reapops-api:local reapops-worker:local --cluster $Cluster }

Step "Creating the namespace and secrets"
Run { kubectl create namespace reapops --dry-run=client -o yaml | kubectl apply -f - | Out-Null }
Run { kubectl -n reapops create secret generic reapops-secrets "--from-env-file=$envFile" --dry-run=client -o yaml | kubectl apply -f - | Out-Null }

if ($Local) {
    Step "Installing ReapOps with Helm from the working tree"
    Run { helm upgrade --install reapops (Join-Path $root "deploy\chart\reapops") -n reapops --wait --timeout 10m }
} else {
    Step "Registering the ReapOps application in Argo CD (revision $Revision)"
    $app = (Get-Content (Join-Path $PSScriptRoot "argocd\reapops.yaml") -Raw) -replace "targetRevision: main", "targetRevision: $Revision"
    $app | kubectl apply -f - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not register the Argo CD application" }
}

Write-Host ""
Write-Host "Console  http://ops.localhost:8080  (password: CONSOLE_PASSWORD in deploy/.env)"
