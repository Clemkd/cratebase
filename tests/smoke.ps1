# End-to-end test of the engine, against a running instance.
#   dotnet run --project src/Cratebase.App --urls http://localhost:8090
#   pwsh tests/smoke.ps1
#
# Covers the nominal path AND the hostile paths: that's where the value of the test lies.

param(
    [string]$BaseUrl = 'http://localhost:8090',
    [string]$Email = 'admin@cratebase.local',
    [string]$Password = 'local-development-0000'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$anonHeaders = @{ 'Content-Type' = 'application/json' }
$api = "$BaseUrl/api"
$rec = "$api/collections/posts/records"

$session = Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = $Email; password = $Password } | ConvertTo-Json)

$adminHeaders = @{ 'Authorization' = "Bearer $($session.token)"; 'Content-Type' = 'application/json' }

$script:passed = 0
$script:failed = 0

function Assert($label, $condition, $detail = '') {
    if ($condition) {
        $script:passed++
        Write-Host "  OK   $label $detail" -ForegroundColor Green
    }
    else {
        $script:failed++
        Write-Host "  FAIL $label $detail" -ForegroundColor Red
    }
}

function StatusOf([scriptblock]$action) {
    try { & $action | Out-Null; return 200 }
    catch { return $_.Exception.Response.StatusCode.value__ }
}

function Encode($value) { [uri]::EscapeDataString($value) }

# SSE reader: curl.exe as a background process, output read back from a file. PowerShell has no
# event-stream client, and hand-rolling an async HttpClient here would cost more than it proves.
function Start-Sse([string]$Url, [string]$Token) {
    $file = Join-Path ([IO.Path]::GetTempPath()) "cratebase-sse-$([Guid]::NewGuid().ToString('N')).txt"
    $arguments = @('-s', '-N', $Url)
    if ($Token) { $arguments += @('-H', "Authorization: $Token") }

    $process = Start-Process -FilePath 'curl.exe' -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput $file

    return [pscustomobject]@{ Process = $process; File = $file }
}

function Read-Sse($stream) {
    if (-not (Test-Path $stream.File)) { return '' }
    return Get-Content $stream.File -Raw -ErrorAction SilentlyContinue
}

function Stop-Sse($stream) {
    try { Stop-Process -Id $stream.Process.Id -Force -ErrorAction SilentlyContinue } catch { }
    Remove-Item $stream.File -Force -ErrorAction SilentlyContinue
}

# Client-side TOTP, to exercise the two-factor flow end to end.
# The server implementation is already proven by the RFC 6238 vectors in the unit tests; this one
# only needs to produce a valid code.
function Get-TotpCode([string]$Secret) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = ''
    foreach ($character in $Secret.TrimEnd('=').ToUpperInvariant().ToCharArray()) {
        $bits += [Convert]::ToString($alphabet.IndexOf($character), 2).PadLeft(5, '0')
    }
    $bytes = for ($i = 0; $i + 8 -le $bits.Length; $i += 8) {
        [Convert]::ToByte($bits.Substring($i, 8), 2)
    }

    $counter = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $counterBytes = [BitConverter]::GetBytes($counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($counterBytes) }

    $hmac = [System.Security.Cryptography.HMACSHA1]::new([byte[]]$bytes)
    $hash = $hmac.ComputeHash($counterBytes)
    $hmac.Dispose()

    $offset = $hash[$hash.Length - 1] -band 0x0F
    $binary = (($hash[$offset] -band 0x7F) -shl 24) -bor
              (($hash[$offset + 1] -band 0xFF) -shl 16) -bor
              (($hash[$offset + 2] -band 0xFF) -shl 8) -bor
               ($hash[$offset + 3] -band 0xFF)

    return ($binary % 1000000).ToString('D6')
}

Write-Host "`n== Availability and authentication ==" -ForegroundColor Cyan
$health = Invoke-RestMethod "$api/health"
Assert 'the engine responds' ($health.status -eq 'ok') "(engine = $($health.engine))"
Assert 'the superadmin is authenticated' ($session.token.Length -gt 20)
Assert 'the password never comes out' ($null -eq $session.record.password)
Assert 'the token key never comes out' ($null -eq $session.record.token_key)

$me = Invoke-RestMethod "$api/me" -Headers $adminHeaders
Assert '/me recognizes the superadmin' ($me.isSuperuser -eq $true)
Assert 'a wrong password is refused' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $Email; password = 'wrong' } | ConvertTo-Json)
        }) -eq 400)
Assert 'an unknown account is refused the same way' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = 'unknown@x.example'; password = 'wrong' } | ConvertTo-Json)
        }) -eq 400)
Assert 'an invalid token counts as anonymous' ((StatusOf {
            Invoke-RestMethod "$api/collections" -Headers @{ Authorization = 'Bearer whatever' }
        }) -eq 401)
# The _superusers rules are locked: it's only readable by a superadmin, who must be able to
# administer their peers from the console. Secrets stay protected by hidden fields, not by the
# rule.
Assert 'an anonymous caller cannot see superadmins' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records" -Headers $anonHeaders
        }) -eq 403)
$superusers = Invoke-RestMethod "$api/collections/_superusers/records" -Headers $adminHeaders
Assert 'the superadmin administers its peers' ($superusers.totalItems -ge 1)
Assert 'no password hash ever comes out' ($null -eq $superusers.items[0].password)

Write-Host "`n== Schema ==" -ForegroundColor Cyan
$definition = @{
    name    = 'posts'; type = 'Base'
    fields  = @(
        @{ name = 'title'; type = 'Text'; required = $true; options = @{ max = 120 } },
        @{ name = 'views'; type = 'Number'; options = @{ min = 0 } },
        @{ name = 'online'; type = 'Bool' },
        @{ name = 'tags'; type = 'Select'; maxSelect = 5; options = @{ values = @('news', 'tech', 'life') } }
    )
    indexes = @(@{ name = 'idx_posts_title'; fields = @('title'); unique = $true })
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}

$existing = try { Invoke-RestMethod "$api/collections/posts" -Headers $adminHeaders } catch { $null }
if ($existing) { Invoke-RestMethod "$api/collections/posts" -Method Delete -Headers $adminHeaders | Out-Null }

$collection = Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders `
    -Body ($definition | ConvertTo-Json -Depth 8)

Assert 'the collection is created' ($collection.name -eq 'posts')
Assert 'the system fields are set' ($collection.fields.Count -eq 7) "($($collection.fields.Count) fields)"
Assert 'creation requires the superadmin' ((StatusOf { Invoke-RestMethod "$api/collections" -Method Post -Headers $anonHeaders -Body ($definition | ConvertTo-Json -Depth 8) }) -eq 401)
Assert 'the roles collection exists' ($null -ne (Invoke-RestMethod "$api/collections" -Headers $adminHeaders).items.Where({ $_.name -eq '_roles' }, 'First')[0])

Write-Host "`n== Writing and reading ==" -ForegroundColor Cyan
$seed = @(
    @{ title = 'Daily news'; views = 120; online = $true; tags = @('news') },
    @{ title = 'Technical guide'; views = 45; online = $true; tags = @('tech', 'news') },
    @{ title = 'Draft'; views = 0; online = $false; tags = @() }
)
foreach ($post in $seed) {
    Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body ($post | ConvertTo-Json -Depth 5) | Out-Null
}

$all = Invoke-RestMethod $rec -Headers $adminHeaders
Assert 'all three records are read back' ($all.totalItems -eq 3)

Write-Host "`n== Filter language ==" -ForegroundColor Cyan
Assert 'numeric comparison' ((Invoke-RestMethod "$rec`?filter=$(Encode 'views > 50')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'contains, case-insensitive' ((Invoke-RestMethod "$rec`?filter=$(Encode "title ~ 'GUIDE'")" -Headers $adminHeaders).totalItems -eq 1)
Assert 'at least one element (?=)' ((Invoke-RestMethod "$rec`?filter=$(Encode "tags ?= 'tech'")" -Headers $adminHeaders).totalItems -eq 1)
Assert 'conjunction' ((Invoke-RestMethod "$rec`?filter=$(Encode 'online = true && views > 100')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'boolean' ((Invoke-RestMethod "$rec`?filter=$(Encode 'online = false')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'a field outside the schema is refused' ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode 'secret = 1')" -Headers $adminHeaders }) -eq 400)
Assert 'an injection does not parse' ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode "title = 'a'; DROP TABLE posts;--")" -Headers $adminHeaders }) -eq 400)
Assert 'sorting outside the schema is refused' ((StatusOf { Invoke-RestMethod "$rec`?sort=-secret" -Headers $adminHeaders }) -eq 400)

$sorted = Invoke-RestMethod "$rec`?sort=-views&fields=title,views&skipTotal=1" -Headers $adminHeaders
Assert 'descending sort is applied' ($sorted.items[0].views -eq 120)
Assert 'the projection strips fields' ($null -eq $sorted.items[0].online)
Assert 'skipTotal avoids the count' ($sorted.totalItems -eq -1)

Write-Host "`n== Automatic dates ==" -ForegroundColor Cyan

# The autodate type isn't reserved for the engine: any collection can declare one.
$auto = @{
    name    = 'autodate_test'; type = 'Base'
    fields  = @(
        @{ name = 'label'; type = 'Text'; required = $true; options = @{} },
        @{ name = 'seen_at'; type = 'AutoDate'; options = @{ onCreate = $true; onUpdate = $true } },
        @{ name = 'created_at'; type = 'AutoDate'; options = @{ onCreate = $true } }
    )
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}

$leftover = try { Invoke-RestMethod "$api/collections/autodate_test" -Headers $adminHeaders } catch { $null }
if ($leftover) { Invoke-RestMethod "$api/collections/autodate_test" -Method Delete -Headers $adminHeaders | Out-Null }

Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($auto | ConvertTo-Json -Depth 8) | Out-Null

# The submitted date is overwritten: a field claimed to be automatic whose value the client sets
# no longer proves anything about when the write actually happened.
$row = Invoke-RestMethod "$api/collections/autodate_test/records" -Method Post -Headers $adminHeaders `
    -Body (@{ label = 'test'; seen_at = '1999-01-01T00:00:00.000Z' } | ConvertTo-Json)

# ConvertFrom-Json returns [datetime] for ISO instants: we compare dates, not strings, otherwise
# we'd be testing the machine's display format.
Assert 'a user-facing autodate is filled at creation' ([datetime]$row.seen_at -gt [datetime]'2020-01-01')
Assert 'the submitted value is overwritten' (([datetime]$row.seen_at).Year -ne 1999)
Assert 'a create-only autodate is also filled' ([datetime]$row.created_at -gt [datetime]'2020-01-01')

Start-Sleep -Milliseconds 1100
$modified = Invoke-RestMethod "$api/collections/autodate_test/records/$($row.id)" -Method Patch `
    -Headers $adminHeaders -Body (@{ label = 'modified test' } | ConvertTo-Json)

Assert 'an on-update autodate is refreshed' ($modified.seen_at -gt $row.seen_at)
# One set only at creation must not move, otherwise "created at" means nothing.
Assert 'a create-only autodate does not move' ($modified.created_at -eq $row.created_at)

Invoke-RestMethod "$api/collections/autodate_test" -Method Delete -Headers $adminHeaders | Out-Null

Write-Host "`n== Access rules ==" -ForegroundColor Cyan
$restricted = $definition.Clone()
$restricted.rules = @{ list = 'online = true'; view = 'online = true'; create = ''; update = 'online = true'; delete = 'online = true' }
$restricted.fields = $collection.fields | Where-Object { -not $_.isSystem } | ForEach-Object {
    @{ id = $_.id; name = $_.name; type = $_.type; required = $_.required; maxSelect = $_.maxSelect; options = $_.options }
}
Invoke-RestMethod "$api/collections/posts" -Method Patch -Headers $adminHeaders -Body ($restricted | ConvertTo-Json -Depth 8) | Out-Null

Assert 'the rule filters the list' ((Invoke-RestMethod $rec -Headers $anonHeaders).totalItems -eq 2)
Assert 'the superadmin bypasses the rule' ((Invoke-RestMethod $rec -Headers $adminHeaders).totalItems -eq 3)

$draft = (Invoke-RestMethod $rec -Headers $adminHeaders).items | Where-Object { $_.title -eq 'Draft' }
Assert 'out-of-scope view: 404, not 403' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Headers $anonHeaders }) -eq 404)
Assert 'out-of-scope update is refused' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Method Patch -Headers $anonHeaders -Body '{"title":"hijacked"}' }) -eq 404)
Assert 'out-of-scope delete is refused' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Method Delete -Headers $anonHeaders }) -eq 404)

# THE test: the targeted row must still exist, and intact.
$survivor = (Invoke-RestMethod $rec -Headers $adminHeaders).items | Where-Object { $_.id -eq $draft.id }
Assert 'the out-of-scope row survives' ($null -ne $survivor)
Assert 'the out-of-scope row is intact' ($survivor.title -eq 'Draft')

Write-Host "`n== Validation and integrity ==" -ForegroundColor Cyan
Assert 'duplicate on unique index: 409' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"Draft"}' }) -eq 409)
Assert 'missing required field: 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"views":5}' }) -eq 400)
Assert 'value outside the list: 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"x","tags":["nonexistent"]}' }) -eq 400)
Assert 'unknown submitted field: 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"y","nonexistent":1}' }) -eq 400)

Write-Host "`n== Schema evolution ==" -ForegroundColor Cyan
$renamed = $restricted.Clone()
$renamed.rules = @{ list = ''; view = ''; update = ''; delete = '' }   # create absent = locked
$renamed.indexes = @(@{ name = 'idx_posts_title'; fields = @('headline'); unique = $true })
$renamed.fields = @($restricted.fields | ForEach-Object {
        $copy = $_.Clone()
        if ($copy.name -eq 'title') { $copy.name = 'headline' }   # rename, identifier preserved
        $copy
    })
$renamed.fields += @{ name = 'summary'; type = 'Text'; required = $false; maxSelect = 1; options = @{} }

Invoke-RestMethod "$api/collections/posts" -Method Patch -Headers $adminHeaders -Body ($renamed | ConvertTo-Json -Depth 8) | Out-Null

$afterRename = Invoke-RestMethod $rec -Headers $adminHeaders
Assert 'the rename preserves the rows' ($afterRename.totalItems -eq 3)
Assert 'the rename preserves the values' (($afterRename.items.headline | Where-Object { $_ -eq 'Draft' }).Count -eq 1)
Assert 'the added field is present' ($afterRename.items[0].PSObject.Properties.Name -contains 'summary')
Assert 'the filter follows the new name' ((Invoke-RestMethod "$rec`?filter=$(Encode "headline ~ 'guide'")" -Headers $adminHeaders).totalItems -eq 1)
Assert "the old name no longer exists" ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode "title = 'x'")" -Headers $adminHeaders }) -eq 400)
Assert 'locked rule (null): 403' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $anonHeaders -Body '{"headline":"attempt"}' }) -eq 403)

Write-Host "`n== Local accounts and RBAC ==" -ForegroundColor Cyan
$members = @{
    name    = 'members'; type = 'Auth'
    fields  = @(@{ name = 'displayName'; type = 'Text'; required = $false; maxSelect = 1; options = @{} })
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}
$old = try { Invoke-RestMethod "$api/collections/members" -Headers $adminHeaders } catch { $null }
if ($old) { Invoke-RestMethod "$api/collections/members" -Method Delete -Headers $adminHeaders | Out-Null }
Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($members | ConvertTo-Json -Depth 8) | Out-Null

$signup = @{ email = 'member@example.com'; password = 'solid-password'; password_confirm = 'solid-password'; displayName = 'Member' }
$account = Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders -Body ($signup | ConvertTo-Json)
Assert 'an account can be created' ($account.email -eq 'member@example.com')
Assert 'the hash does not come out at creation' ($null -eq $account.password)
Assert 'the account is not self-verified' ($account.verified -eq $false)
Assert "the account has no permission at sign-up" ($account.permissions.Count -eq 0)

Assert 'a too-short password is refused' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
                -Body (@{ email = 'short@example.com'; password = 'short' } | ConvertTo-Json)
        }) -eq 400)
Assert 'a mismatched confirmation is refused' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
                -Body (@{ email = 'x@example.com'; password = 'solid-password'; password_confirm = 'something-else' } | ConvertTo-Json)
        }) -eq 400)

# THE privilege escalation test: granting yourself rights by signing up.
$escalation = Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
    -Body (@{ email = 'attacker@example.com'; password = 'solid-password'; permissions = @('*'); roles = @('admin') } | ConvertTo-Json)
Assert "self-assigned permissions are ignored" ($escalation.permissions.Count -eq 0)
Assert "self-assigned roles are ignored" ($escalation.roles.Count -eq 0)

$memberSession = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = 'member@example.com'; password = 'solid-password' } | ConvertTo-Json)
$memberHeaders = @{ 'Authorization' = "Bearer $($memberSession.token)"; 'Content-Type' = 'application/json' }
Assert 'the account can sign in' ($memberSession.token.Length -gt 20)
Assert "the account is not a superadmin" ((Invoke-RestMethod "$api/me" -Headers $memberHeaders).isSuperuser -eq $false)
Assert "the account cannot administer collections" ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $memberHeaders }) -eq 403)

# Role, then assignment by the superadmin.
# The role is removed if it survives a previous run: the suite must be re-runnable against an
# already-populated database, otherwise it's only usable once.
$existingRole = (Invoke-RestMethod "$api/collections/_roles/records?filter=$(Encode "name = 'editor'")" -Headers $adminHeaders).items
foreach ($role in $existingRole) {
    Invoke-RestMethod "$api/collections/_roles/records/$($role.id)" -Method Delete -Headers $adminHeaders | Out-Null
}

Invoke-RestMethod "$api/collections/_roles/records" -Method Post -Headers $adminHeaders `
    -Body (@{ name = 'editor'; grants = @('posts.write', 'posts.publish') } | ConvertTo-Json) | Out-Null
Invoke-RestMethod "$api/collections/members/records/$($account.id)/grants" -Method Post -Headers $adminHeaders `
    -Body (@{ roles = @('editor'); permissions = @('media.upload') } | ConvertTo-Json) | Out-Null

Assert "the assignment revokes the account's sessions" ((StatusOf { Invoke-RestMethod "$api/me" -Headers $memberHeaders }) -eq 401)

$memberSession = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = 'member@example.com'; password = 'solid-password' } | ConvertTo-Json)
$memberHeaders = @{ 'Authorization' = "Bearer $($memberSession.token)"; 'Content-Type' = 'application/json' }
$identity = Invoke-RestMethod "$api/me" -Headers $memberHeaders
Assert 'the role permissions are resolved' ($identity.permissions -contains 'posts.write')
Assert 'individual overrides are merged in' ($identity.permissions -contains 'media.upload')
Assert "an unassigned role grants nothing" (-not ($identity.permissions -contains 'users.manage'))

Assert "an account cannot grant itself rights" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records/$($account.id)/grants" -Method Post `
                -Headers $memberHeaders -Body (@{ roles = @(); permissions = @('*') } | ConvertTo-Json)
        }) -eq 403)

Write-Host "`n== Two-factor authentication ==" -ForegroundColor Cyan
$methods = Invoke-RestMethod "$api/collections/members/auth-methods" -Headers $anonHeaders
Assert 'password is a method' ($methods.password -eq $true)
Assert 'no unconfigured external provider' ($methods.oauth2.Count -eq 0)
Assert "an unknown provider is refused" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-oauth2" -Method Post -Headers $anonHeaders `
                -Body (@{ provider = 'nonexistent'; code = 'x'; redirectUrl = 'http://localhost' } | ConvertTo-Json)
        }) -eq 400)

$enrollment = Invoke-RestMethod "$api/collections/members/mfa/enroll" -Method Post -Headers $memberHeaders
Assert 'a secret is produced' ($enrollment.secret.Length -eq 32)
Assert "the enrollment uri is usable" ($enrollment.uri -like 'otpauth://totp/*algorithm=SHA1*')

Assert 'a wrong code does not confirm' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/mfa/confirm" -Method Post -Headers $memberHeaders `
                -Body (@{ code = '000000' } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/collections/members/mfa/confirm" -Method Post -Headers $memberHeaders `
    -Body (@{ code = Get-TotpCode $enrollment.secret } | ConvertTo-Json) | Out-Null

# THE test: the password alone must no longer return a token.
$firstFactor = $null
$challengeStatus = try {
    Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post -Headers $anonHeaders `
        -Body (@{ identity = 'member@example.com'; password = 'solid-password' } | ConvertTo-Json) | Out-Null
    200
}
catch {
    $firstFactor = $_.ErrorDetails.Message | ConvertFrom-Json
    $_.Exception.Response.StatusCode.value__
}

Assert 'the password alone is no longer enough' ($challengeStatus -eq 401)
Assert 'a challenge is opened' ($null -ne $firstFactor.mfaId)
Assert "no token is issued on the first factor" ($null -eq $firstFactor.token)

Assert 'a wrong second factor is refused' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
                -Body (@{ mfaId = $firstFactor.mfaId; code = '000000' } | ConvertTo-Json)
        }) -eq 400)

$secondFactor = Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
    -Body (@{ mfaId = $firstFactor.mfaId; code = (Get-TotpCode $enrollment.secret) } | ConvertTo-Json)
Assert 'the second factor opens the session' ($secondFactor.token.Length -gt 20)

# A replayable challenge would turn a single interception into permanent access.
Assert "the challenge is not replayable" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
                -Body (@{ mfaId = $firstFactor.mfaId; code = (Get-TotpCode $enrollment.secret) } | ConvertTo-Json)
        }) -eq 400)

$mfaHeaders = @{ 'Authorization' = "Bearer $($secondFactor.token)"; 'Content-Type' = 'application/json' }
Assert 'disabling requires a valid code' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/mfa/disable" -Method Post -Headers $mfaHeaders `
                -Body (@{ code = '000000' } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/collections/members/mfa/disable" -Method Post -Headers $mfaHeaders `
    -Body (@{ code = Get-TotpCode $enrollment.secret } | ConvertTo-Json) | Out-Null

$restored = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post -Headers $anonHeaders `
    -Body (@{ identity = 'member@example.com'; password = 'solid-password' } | ConvertTo-Json)
Assert 'after disabling, the password alone is enough again' ($restored.token.Length -gt 20)

Write-Host "`n== Files ==" -ForegroundColor Cyan
$bearer = @{ Authorization = "Bearer $($session.token)" }
$documents = @{
    name    = 'documents'; type = 'Base'
    fields  = @(
        @{ name = 'title'; type = 'Text'; required = $true; maxSelect = 1; options = @{} },
        @{ name = 'image'; type = 'File'; required = $false; maxSelect = 1; options = @{ maxFileSize = 2000000 } },
        @{ name = 'attachments'; type = 'File'; required = $false; maxSelect = 5; options = @{} }
    )
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}
try { Invoke-RestMethod "$api/collections/documents" -Method Delete -Headers $adminHeaders | Out-Null } catch {}
Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($documents | ConvertTo-Json -Depth 8) | Out-Null

# A real image, so thumbnail generation has something to decode.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 200, 120
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::CornflowerBlue)
$graphics.Dispose()
$source = Join-Path $env:TEMP 'cratebase-smoke.png'
$bitmap.Save($source, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$copies = 1..3 | ForEach-Object {
    $copy = Join-Path $env:TEMP "cratebase-smoke-$_.png"
    Copy-Item $source $copy -Force
    $copy
}

$document = Invoke-RestMethod "$api/collections/documents/records" -Method Post -Headers $bearer `
    -Form @{ title = 'Report'; image = Get-Item $source }
Assert 'a file is imported' ($document.image -match '\.png$')
Assert "the name receives a random suffix" ($document.image -ne 'cratebase-smoke.png')

$fileUrl = "$api/files/documents/$($document.id)/$($document.image)"
$served = Invoke-WebRequest $fileUrl -UseBasicParsing
Assert 'the file is served' ($served.StatusCode -eq 200)
Assert 'the MIME type is correct' ($served.Headers['Content-Type'] -contains 'image/png')

Assert 'a thumbnail is produced' ((Invoke-WebRequest "$fileUrl`?thumb=100x100" -UseBasicParsing).StatusCode -eq 200)
Assert 'a free-ratio thumbnail is produced' ((Invoke-WebRequest "$fileUrl`?thumb=0x60" -UseBasicParsing).StatusCode -eq 200)
Assert 'an oversized thumbnail is refused' ((StatusOf { Invoke-WebRequest "$fileUrl`?thumb=99999x99999" -UseBasicParsing }) -eq 400)
Assert 'a directory traversal is refused' ((StatusOf { Invoke-WebRequest "$api/files/documents/$($document.id)/..%2F..%2Fcratebase.db" -UseBasicParsing }) -eq 404)

$folder = Invoke-RestMethod "$api/collections/documents/records" -Method Post -Headers $bearer `
    -Form @{ title = 'Folder'; attachments = @((Get-Item $copies[0]), (Get-Item $copies[1])) }
Assert 'several files are imported' ($folder.attachments.Count -eq 2)

$appended = Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Patch `
    -Headers $bearer -Form @{ 'attachments+' = Get-Item $copies[2] }
Assert "the '+' modifier appends without replacing" ($appended.attachments.Count -eq 3)

$trimmed = Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Patch `
    -Headers $bearer -Form @{ 'attachments-' = $appended.attachments[0] }
Assert "the '-' modifier removes a single file" ($trimmed.attachments.Count -eq 2)

$oversize = Join-Path $env:TEMP 'cratebase-smoke-big.bin'
[IO.File]::WriteAllBytes($oversize, (New-Object byte[] 3000000))
Assert 'an oversized file is refused' ((StatusOf {
            Invoke-RestMethod "$api/collections/documents/records" -Method Post -Headers $bearer `
                -Form @{ title = 'Too big'; image = Get-Item $oversize }
        }) -eq 400)

# The bytes follow the row: without this, we'd bill for and back up files nothing points to
# anymore.
#
# Verified through the API, not the disk: the test must hold equally for local storage and for
# S3, otherwise half the scalability promise would stay uncovered.
$orphanUrl = "$api/files/documents/$($folder.id)/$($trimmed.attachments[0])"
Assert 'the bytes are served before deletion' ((StatusOf { Invoke-WebRequest $orphanUrl -UseBasicParsing }) -eq 200)
Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Delete -Headers $adminHeaders | Out-Null
Assert 'deletion takes the bytes with it' ((StatusOf { Invoke-WebRequest $orphanUrl -UseBasicParsing }) -eq 404)

Remove-Item $source, $oversize -ErrorAction SilentlyContinue
$copies | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }

Write-Host "`n== Revocation ==" -ForegroundColor Cyan
$temporary = Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = $Email; password = $Password } | ConvertTo-Json)
$temporaryHeaders = @{ 'Authorization' = "Bearer $($temporary.token)"; 'Content-Type' = 'application/json' }

Assert 'the second token works' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $temporaryHeaders }) -eq 200)
Invoke-RestMethod "$api/collections/_superusers/auth-logout" -Method Post -Headers $temporaryHeaders | Out-Null
# What opaque tokens buy, and a self-signed JWT can't offer.
Assert 'logout actually revokes the token' ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $temporaryHeaders }) -eq 401)
Assert "the original token stays valid" ((StatusOf { Invoke-RestMethod "$api/me" -Headers $adminHeaders }) -eq 200)

Write-Host "`n== Superusers ==" -ForegroundColor Cyan

# The bootstrap account is the only superuser: deleting it would make the instance
# unadministrable, since every system collection is locked and bootstrapping only recreates an
# account if configuration carries one.
$self = Invoke-RestMethod "$api/me" -Headers $adminHeaders
Assert 'the last superuser cannot be deleted' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records/$($self.id)" -Method Delete -Headers $adminHeaders
        }) -eq 409)
Assert 'the account is still there' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $adminHeaders }) -eq 200)

$secondEmail = "second-$([Guid]::NewGuid().ToString('N').Substring(0, 8))@cratebase.local"
$second = Invoke-RestMethod "$api/collections/_superusers/records" -Method Post -Headers $adminHeaders `
    -Body (@{ email = $secondEmail; password = 'initial-password'; password_confirm = 'initial-password' } | ConvertTo-Json)

Assert 'a second superuser can be created' ($null -ne $second.id)

$secondSession = Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'initial-password' } | ConvertTo-Json)
$secondHeaders = @{ 'Authorization' = "Bearer $($secondSession.token)"; 'Content-Type' = 'application/json' }

Assert 'the second account administers' ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $secondHeaders }) -eq 200)

# Changing a password must close open sessions: without this, changing your password after a
# session theft wouldn't sign the thief out, even though that's the user's first instinct — and
# they'd believe the problem solved.
Invoke-RestMethod "$api/collections/_superusers/records/$($second.id)" -Method Patch -Headers $adminHeaders `
    -Body (@{ password = 'replaced-password'; password_confirm = 'replaced-password' } | ConvertTo-Json) | Out-Null

Assert 'the password change revokes sessions' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $secondHeaders }) -eq 401)
Assert "the old password is worthless" ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'initial-password' } | ConvertTo-Json)
        }) -eq 400)
Assert 'the new password opens a session' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'replaced-password' } | ConvertTo-Json)
        }) -eq 200)

# As long as two remain, deletion is allowed: the guard is about the last one, not about any of
# them.
Assert 'one of two superusers can be deleted' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records/$($second.id)" -Method Delete -Headers $adminHeaders
        }) -eq 200)

Write-Host "`n== Logs and settings ==" -ForegroundColor Cyan

Assert 'the log is refused to an anonymous caller' ((StatusOf { Invoke-RestMethod "$api/logs" -Headers $anonHeaders }) -eq 401)
Assert "instance status is refused to an anonymous caller" ((StatusOf { Invoke-RestMethod "$api/instance" -Headers $anonHeaders }) -eq 401)
Assert 'settings are refused to an anonymous caller' ((StatusOf { Invoke-RestMethod "$api/settings" -Headers $anonHeaders }) -eq 401)

$log = Invoke-RestMethod "$api/logs?perPage=200&stats=1&granularity=Hour" -Headers $adminHeaders
$histogram = ($log.stats.items | Measure-Object -Property count -Sum).Sum

# Page and histogram come from the same call: two calls would each flush the write buffer
# independently, and the chart would announce a total the table beneath it doesn't show.
Assert 'page and histogram count the same' ($log.totalItems -eq $histogram) "($($log.totalItems) / $histogram)"
Assert 'the suite was indeed logged' ($log.totalItems -gt 0)

$refusals = Invoke-RestMethod "$api/logs?level=Warning&perPage=200" -Headers $adminHeaders
Assert 'refusals are logged as warnings' (($refusals.items | Where-Object { $_.status -eq 401 }).Count -gt 0)

# The level filter is a set, not a lower bound: asking for warnings alone must return no errors,
# otherwise isolating 4xx from real outages becomes impossible again.
Assert 'a single level excludes the others' (($refusals.items | Where-Object { $_.level -ne 'Warning' }).Count -eq 0)
$severe = Invoke-RestMethod "$api/logs?level=Warning,Error&perPage=200" -Headers $adminHeaders
Assert 'several levels combine' ($severe.totalItems -ge $refusals.totalItems)
Assert 'no level outside the requested set' (($severe.items | Where-Object { $_.level -notin @('Warning', 'Error') }).Count -eq 0)
Assert 'no log read logs itself' (($log.items | Where-Object { $_.url -like '/api/logs*' }).Count -eq 0)

# The file token travels in the URL: its lifetime is two minutes, but writing it in the clear
# into a table kept for several days would undo the precaution.
$secret = "token-not-to-be-logged-$([Guid]::NewGuid().ToString('N'))"
StatusOf { Invoke-WebRequest "$api/files/documents/nonexistent/absent.png?token=$secret" -UseBasicParsing } | Out-Null
Start-Sleep -Milliseconds 200
$leak = Invoke-RestMethod "$api/logs?q=$(Encode $secret)" -Headers $adminHeaders
Assert 'the file token is masked in the log' ($leak.totalItems -eq 0)
$masked = Invoke-RestMethod "$api/logs?q=absent.png" -Headers $adminHeaders
Assert "the rest of the URL is kept" (($masked.items | Where-Object { $_.url -like '*token=***' }).Count -gt 0)

$settings = Invoke-RestMethod "$api/settings" -Headers $adminHeaders
Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ appName = 'Cratebase — staging' } | ConvertTo-Json) | Out-Null
$after = Invoke-RestMethod "$api/settings" -Headers $adminHeaders

# A partial PATCH must not reset anything by default: a screen that only sends the name would
# otherwise reset retention and address collection, with no warning at all.
Assert 'the partial PATCH does not reset siblings' (
    $after.appName -eq 'Cratebase — staging' -and
    $after.logs.retentionDays -eq $settings.logs.retentionDays -and
    $after.logs.logIp -eq $settings.logs.logIp)

Assert 'an out-of-bounds retention is refused' ((StatusOf {
            Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders -Body (@{ logs = @{ retentionDays = 900 } } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ appName = $settings.appName } | ConvertTo-Json) | Out-Null

Write-Host "`n== Realtime ==" -ForegroundColor Cyan

$stream = Start-Sse "$api/realtime" "Bearer $($session.token)"
Start-Sleep -Milliseconds 900

$greeting = Read-Sse $stream
Assert 'the stream announces a client identifier' ($greeting -match 'event: connect')

$clientId = if ($greeting -match '"clientId":"([^"]+)"') { $Matches[1] } else { '' }
Assert 'the client identifier is usable' ($clientId.Length -gt 10)

Invoke-RestMethod "$api/realtime" -Method Post -Headers $adminHeaders `
    -Body (@{ clientId = $clientId; subscriptions = @('posts') } | ConvertTo-Json) | Out-Null

$live = Invoke-RestMethod $rec -Method Post -Headers $adminHeaders `
    -Body (@{ title = 'broadcast live'; views = 7; online = $true; tags = @() } | ConvertTo-Json -Depth 5)

Start-Sleep -Milliseconds 900
$received = Read-Sse $stream

Assert 'a creation is broadcast' ($received -match 'event: posts')
Assert 'the event carries the action' ($received -match '"action":"create"')
# The whole record travels: without it, every subscriber would have to re-read the row, and a
# hundred subscribers would produce a hundred reads per write.
Assert 'the event carries the record' ($received -match 'broadcast live')

Invoke-RestMethod "$rec/$($live.id)" -Method Delete -Headers $adminHeaders | Out-Null
Start-Sleep -Milliseconds 700
Assert 'a deletion is broadcast' ((Read-Sse $stream) -match '"action":"delete"')

Stop-Sse $stream

# The point that decides the value of everything else: subscribing must not bypass access rules.
# The superusers collection is locked; an anonymous caller subscribed to it must receive nothing.
$anonymous = Start-Sse "$api/realtime" ''
Start-Sleep -Milliseconds 900
$anonGreeting = Read-Sse $anonymous
$anonId = if ($anonGreeting -match '"clientId":"([^"]+)"') { $Matches[1] } else { '' }

Invoke-RestMethod "$api/realtime" -Method Post -Headers $anonHeaders `
    -Body (@{ clientId = $anonId; subscriptions = @('_superusers', 'posts') } | ConvertTo-Json) | Out-Null

$ghost = Invoke-RestMethod "$api/collections/_superusers/records" -Method Post -Headers $adminHeaders `
    -Body (@{ email = "ghost-$([Guid]::NewGuid().ToString('N').Substring(0,8))@example.com"
              password = 'solid-password'; password_confirm = 'solid-password' } | ConvertTo-Json)

Start-Sleep -Milliseconds 900
$seen = Read-Sse $anonymous

Assert 'an anonymous caller gets nothing from a locked collection' ($seen -notmatch 'event: _superusers')

Invoke-RestMethod "$api/collections/_superusers/records/$($ghost.id)" -Method Delete -Headers $adminHeaders | Out-Null
Stop-Sse $anonymous

Assert 'realtime can be switched off from settings' ((Invoke-RestMethod "$api/settings" -Method Patch `
            -Headers $adminHeaders -Body (@{ realtime = @{ enabled = $false } } | ConvertTo-Json -Depth 5)
    ).realtime.enabled -eq $false)

Assert 'a stream is refused while realtime is off' ((StatusOf {
            Invoke-RestMethod "$api/realtime" -Headers $adminHeaders
        }) -eq 400)

Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ realtime = @{ enabled = $true } } | ConvertTo-Json -Depth 5) | Out-Null

Assert 'an out-of-bounds stream count is refused' ((StatusOf {
            Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
                -Body (@{ realtime = @{ maxClients = 0 } } | ConvertTo-Json -Depth 5)
        }) -eq 400)

Write-Host "`n== Storage ==" -ForegroundColor Cyan

$store = Invoke-RestMethod "$api/storage" -Headers $adminHeaders
Assert 'the store describes itself' ($store.kind -in @('local', 's3'))

# No path should ever return a secret key: the console can read the configuration, never the
# value that would let it be used elsewhere.
Assert 'no secret key leaves the process' (
    -not ($store.PSObject.Properties.Name -contains 'secretKey') -and
    -not ($store.PSObject.Properties.Name -contains 'accessKey'))

$objects = Invoke-RestMethod "$api/storage/objects?perPage=200" -Headers $adminHeaders
Assert 'the inventory responds' ($objects.totalItems -ge 0)

# The file imported earlier is still referenced by its record: it must not be counted as an
# orphan, otherwise the detection is worthless.
$live = $objects.items | Where-Object { $_.fileName -eq $document.image -and -not $_.isThumb }
if ($live) {
    Assert 'a referenced file is not an orphan' (-not $live.orphan)

    # And it must not be deletable from the inventory: removing it would leave the record pointing
    # at nothing.
    Assert 'a referenced file cannot be deleted from here' ((StatusOf {
                Invoke-RestMethod "$api/storage/objects" -Method Delete -Headers $adminHeaders `
                    -Body (@{ keys = @($live.key) } | ConvertTo-Json)
            }) -eq 409)
}

$thumbs = Invoke-RestMethod "$api/storage/objects?kind=thumbs&perPage=200" -Headers $adminHeaders
Assert 'the kind filter only returns thumbnails' (
    ($thumbs.items | Where-Object { -not $_.isThumb }).Count -eq 0)

$usage = Invoke-RestMethod "$api/usage" -Headers $adminHeaders
Assert 'the database measures itself' ($usage.database.bytes -gt 0)
Assert 'the engine is named' ($usage.database.engine -in @('sqlite', 'postgres'))
Assert 'files are totaled' ($usage.files.bytes -ge 0 -and $usage.files.objects -ge 0)

# A capacity of zero means "unknown", not "none": neither PostgreSQL nor S3 exposes a limit
# portably, and the screen must then show an unmetered volume.
Assert 'an absent capacity is zero, not a made-up value' (
    $usage.database.capacityBytes -ge 0 -and $usage.files.capacityBytes -ge 0)

if ($usage.host.available) {
    Assert 'the host volume is measured' ($usage.host.totalBytes -gt 0)
    Assert 'free space fits within the total' ($usage.host.freeBytes -le $usage.host.totalBytes)
}

$probe = Invoke-RestMethod "$api/storage/check" -Method Post -Headers $adminHeaders
Assert 'the store passes the end-to-end test' ($probe.ok)
Assert 'the test writes, reads back, and deletes' (($probe.steps | Where-Object { $_.state -eq 'ok' }).Count -ge 4)

$archive = Join-Path ([IO.Path]::GetTempPath()) "cratebase-smoke-$([Guid]::NewGuid().ToString('N')).zip"
$token = (Invoke-RestMethod "$api/files/token" -Method Post -Headers $adminHeaders).token
Invoke-WebRequest "$api/storage/archive?token=$token" -OutFile $archive -UseBasicParsing | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$entries = $zip.Entries.Count
$archivedThumbs = ($zip.Entries | Where-Object { $_.FullName -like '*/thumbs_*' }).Count
$zip.Dispose()
Remove-Item $archive -Force

Assert 'the archive contains files' ($entries -gt 0)
# Thumbnails regenerate: archiving them would amount to archiving a cache, and doubling its
# weight.
Assert 'the archive excludes thumbnails' ($archivedThumbs -eq 0)

Assert 'the archive requires an identity' ((StatusOf { Invoke-WebRequest "$api/storage/archive" -UseBasicParsing }) -eq 401)

Write-Host "`n---------------------------------------------" -ForegroundColor Cyan
Write-Host "  $script:passed passed, $script:failed failed" -ForegroundColor $(if ($script:failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "---------------------------------------------`n" -ForegroundColor Cyan

exit $(if ($script:failed -eq 0) { 0 } else { 1 })
