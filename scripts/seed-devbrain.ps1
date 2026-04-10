#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seed a freshly deployed DevBrain instance with baseline reference documents.

.DESCRIPTION
    Reads the Function App URL from the current azd environment and the MCP
    extension system key from the DEVBRAIN_KEY environment variable (or prompts
    for either if not set), then calls the DevBrain UpsertDocument MCP tool
    over the SSE transport to seed a set of default documents.

    Run this after `azd up` on a brand new deployment. Safe to re-run — every
    upsert is a full overwrite.

.PARAMETER FunctionUrl
    Base URL of the deployed Function App (e.g. https://devbrain-xyz.azurewebsites.net).
    Defaults to the AZURE_FUNCTION_URL output from the current azd environment.

.PARAMETER FunctionKey
    The MCP extension system key (Azure Portal > Function App > App keys >
    System keys > mcp_extension). Defaults to $env:DEVBRAIN_KEY.

.EXAMPLE
    ./scripts/seed-devbrain.ps1

.EXAMPLE
    ./scripts/seed-devbrain.ps1 -FunctionUrl https://devbrain-xyz.azurewebsites.net -FunctionKey abc123
#>

[CmdletBinding()]
param(
    [string]$FunctionUrl,
    [string]$FunctionKey
)

$ErrorActionPreference = 'Stop'

# ---------- resolve settings ----------

function Get-AzdEnvValue {
    param([string]$Name)
    try {
        $values = azd env get-values 2>$null
        foreach ($line in $values) {
            if ($line -match "^$Name=`"?([^`"]+)`"?$") {
                return $matches[1]
            }
        }
    } catch {
        # azd not installed or no env initialized — fall through
    }
    return $null
}

if (-not $FunctionUrl) {
    $FunctionUrl = Get-AzdEnvValue -Name 'AZURE_FUNCTION_URL'
}
if (-not $FunctionUrl) {
    $FunctionUrl = Read-Host -Prompt 'Function App URL (e.g. https://devbrain-xyz.azurewebsites.net)'
}

if (-not $FunctionKey) {
    $FunctionKey = $env:DEVBRAIN_KEY
}
if (-not $FunctionKey) {
    $secure = Read-Host -Prompt 'DevBrain MCP function key (mcp_extension system key)' -AsSecureString
    $FunctionKey = [System.Net.NetworkCredential]::new('', $secure).Password
}

if (-not $FunctionUrl -or -not $FunctionKey) {
    throw 'FunctionUrl and FunctionKey are required.'
}

$FunctionUrl = $FunctionUrl.TrimEnd('/')
$SseUrl = "$FunctionUrl/runtime/webhooks/mcp/sse"

Write-Host "Seeding DevBrain at $FunctionUrl" -ForegroundColor Cyan

# ---------- minimal MCP client over SSE ----------

Add-Type -AssemblyName System.Net.Http

function Invoke-McpTool {
    param(
        [Parameter(Mandatory)][string]$ToolName,
        [Parameter(Mandatory)][hashtable]$Arguments
    )

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.Add('x-functions-key', $FunctionKey)

    try {
        # 1. Open SSE stream to get the session-scoped message endpoint.
        $sseReq = [System.Net.Http.HttpRequestMessage]::new('GET', $SseUrl)
        $sseReq.Headers.Accept.Add(
            [System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new('text/event-stream'))
        $sseResp = $client.SendAsync(
            $sseReq,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
        if (-not $sseResp.IsSuccessStatusCode) {
            throw "SSE connect failed: HTTP $([int]$sseResp.StatusCode) $($sseResp.ReasonPhrase)"
        }
        $stream = $sseResp.Content.ReadAsStreamAsync().Result
        $reader = [System.IO.StreamReader]::new($stream)

        # 2. Read `event: endpoint` / `data: <url>` frame.
        $messageUrl = $null
        $pendingEvent = $null
        while ($null -eq $messageUrl) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { throw 'SSE stream closed before endpoint event was received.' }
            if ($line.StartsWith('event: ')) {
                $pendingEvent = $line.Substring(7).Trim()
            }
            elseif ($line.StartsWith('data: ') -and $pendingEvent -eq 'endpoint') {
                $path = $line.Substring(6).Trim()
                $messageUrl = if ($path -match '^https?://') { $path } else { "$FunctionUrl$path" }
            }
        }

        $headers = @{
            'x-functions-key' = $FunctionKey
            'Content-Type'    = 'application/json'
        }

        # 3. initialize
        $initBody = @{
            jsonrpc = '2.0'
            id      = 1
            method  = 'initialize'
            params  = @{
                protocolVersion = '2024-11-05'
                capabilities    = @{}
                clientInfo      = @{ name = 'seed-devbrain'; version = '1.0' }
            }
        } | ConvertTo-Json -Depth 10 -Compress
        Invoke-RestMethod -Uri $messageUrl -Method POST -Headers $headers -Body $initBody | Out-Null

        # 4. initialized notification
        $initializedBody = @{
            jsonrpc = '2.0'
            method  = 'notifications/initialized'
        } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri $messageUrl -Method POST -Headers $headers -Body $initializedBody | Out-Null

        # 5. tools/call
        $callBody = @{
            jsonrpc = '2.0'
            id      = 2
            method  = 'tools/call'
            params  = @{
                name      = $ToolName
                arguments = $Arguments
            }
        } | ConvertTo-Json -Depth 20 -Compress
        Invoke-RestMethod -Uri $messageUrl -Method POST -Headers $headers -Body $callBody | Out-Null

        # 6. Read the tool response off the SSE stream (matches id=2).
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $pendingEvent = $null
        while ([DateTime]::UtcNow -lt $deadline) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
            if ($line.StartsWith('event: ')) {
                $pendingEvent = $line.Substring(7).Trim()
                continue
            }
            if ($line.StartsWith('data: ')) {
                $payload = $line.Substring(6)
                try {
                    $parsed = $payload | ConvertFrom-Json -ErrorAction Stop
                    if ($parsed.id -eq 2) { return $parsed }
                } catch {
                    # not a JSON-RPC frame — skip
                }
            }
        }
        throw 'Timed out waiting for tool response on SSE stream.'
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($client) { $client.Dispose() }
    }
}

# ---------- seed documents ----------

$repoRoot = Split-Path -Parent $PSScriptRoot
$seedDir = Join-Path $repoRoot 'docs/seed'

$documents = @(
    @{
        Key     = 'ref:devbrain-usage'
        Project = 'default'
        File    = Join-Path $seedDir 'ref-devbrain-usage.md'
        Tags    = @('meta', 'instructions', 'usage')
    }
)

$failures = 0
foreach ($doc in $documents) {
    if (-not (Test-Path $doc.File)) {
        Write-Host "  [FAIL] $($doc.Key) — seed file not found: $($doc.File)" -ForegroundColor Red
        $failures++
        continue
    }

    $content = Get-Content -Path $doc.File -Raw
    $args = @{
        key     = $doc.Key
        content = $content
        tags    = $doc.Tags
        project = $doc.Project
    }

    try {
        $result = Invoke-McpTool -ToolName 'UpsertDocument' -Arguments $args
        if ($result.error) {
            Write-Host "  [FAIL] $($doc.Key) (project=$($doc.Project)) — $($result.error.message)" -ForegroundColor Red
            $failures++
        } else {
            Write-Host "  [ OK ] $($doc.Key) (project=$($doc.Project))" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "  [FAIL] $($doc.Key) (project=$($doc.Project)) — $($_.Exception.Message)" -ForegroundColor Red
        $failures++
    }
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "Seeding completed with $failures failure(s)." -ForegroundColor Yellow
    exit 1
}
Write-Host "Seeding complete." -ForegroundColor Green
