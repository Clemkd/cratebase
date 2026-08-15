# Test de bout en bout du moteur, contre une instance en cours d'exécution.
#   dotnet run --project src/Cratebase.App --urls http://localhost:8090
#   pwsh tests/smoke.ps1
#
# Couvre le chemin nominal ET les chemins hostiles : c'est là que se joue la valeur du test.

param(
    [string]$BaseUrl = 'http://localhost:8090',
    [string]$Email = 'admin@cratebase.local',
    [string]$Password = 'developpement-local-0000'
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
        Write-Host "  ECHEC $label $detail" -ForegroundColor Red
    }
}

function StatusOf([scriptblock]$action) {
    try { & $action | Out-Null; return 200 }
    catch { return $_.Exception.Response.StatusCode.value__ }
}

function Encode($value) { [uri]::EscapeDataString($value) }

# Lecteur SSE : curl.exe en tache de fond, sortie relue dans un fichier. PowerShell n'a pas de
# client d'evenements, et bricoler un HttpClient asynchrone ici couterait plus que ce qu'il prouve.
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

# TOTP côté client, pour exercer le flot de double authentification de bout en bout.
# L'implémentation serveur est déjà prouvée par les vecteurs de la RFC 6238 dans les tests
# unitaires ; celle-ci n'a qu'à produire un code valide.
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

Write-Host "`n== Disponibilite et authentification ==" -ForegroundColor Cyan
$health = Invoke-RestMethod "$api/health"
Assert 'le moteur repond' ($health.status -eq 'ok') "(moteur = $($health.engine))"
Assert 'le superadmin est authentifie' ($session.token.Length -gt 20)
Assert 'le mot de passe ne sort jamais' ($null -eq $session.record.password)
Assert 'la cle de jeton ne sort jamais' ($null -eq $session.record.token_key)

$me = Invoke-RestMethod "$api/me" -Headers $adminHeaders
Assert '/me reconnait le superadmin' ($me.isSuperuser -eq $true)
Assert 'un mot de passe faux est refuse' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $Email; password = 'faux' } | ConvertTo-Json)
        }) -eq 400)
Assert 'un compte inconnu est refuse de la meme facon' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = 'inconnu@x.fr'; password = 'faux' } | ConvertTo-Json)
        }) -eq 400)
Assert 'un jeton invalide vaut anonyme' ((StatusOf {
            Invoke-RestMethod "$api/collections" -Headers @{ Authorization = 'Bearer nimportequoi' }
        }) -eq 401)
# Les regles de _superusers sont verrouillees : elle n'est donc lisible que par un superadmin,
# qui doit pouvoir administrer ses pairs depuis la console. Les secrets restent proteges par les
# champs masques, pas par la regle.
Assert 'un anonyme ne voit pas les superadmins' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records" -Headers $anonHeaders
        }) -eq 403)
$superusers = Invoke-RestMethod "$api/collections/_superusers/records" -Headers $adminHeaders
Assert 'le superadmin administre ses pairs' ($superusers.totalItems -ge 1)
Assert 'aucun condensat de mot de passe ne sort' ($null -eq $superusers.items[0].password)

Write-Host "`n== Schema ==" -ForegroundColor Cyan
$definition = @{
    name    = 'posts'; type = 'Base'
    fields  = @(
        @{ name = 'title'; type = 'Text'; required = $true; options = @{ max = 120 } },
        @{ name = 'views'; type = 'Number'; options = @{ min = 0 } },
        @{ name = 'online'; type = 'Bool' },
        @{ name = 'tags'; type = 'Select'; maxSelect = 5; options = @{ values = @('actu', 'tech', 'vie') } }
    )
    indexes = @(@{ name = 'idx_posts_title'; fields = @('title'); unique = $true })
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}

$existing = try { Invoke-RestMethod "$api/collections/posts" -Headers $adminHeaders } catch { $null }
if ($existing) { Invoke-RestMethod "$api/collections/posts" -Method Delete -Headers $adminHeaders | Out-Null }

$collection = Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders `
    -Body ($definition | ConvertTo-Json -Depth 8)

Assert 'la collection est creee' ($collection.name -eq 'posts')
Assert 'les champs systeme sont poses' ($collection.fields.Count -eq 7) "($($collection.fields.Count) champs)"
Assert 'la creation exige le superadmin' ((StatusOf { Invoke-RestMethod "$api/collections" -Method Post -Headers $anonHeaders -Body ($definition | ConvertTo-Json -Depth 8) }) -eq 401)
Assert 'la collection des roles existe' ($null -ne (Invoke-RestMethod "$api/collections" -Headers $adminHeaders).items.Where({ $_.name -eq '_roles' }, 'First')[0])

Write-Host "`n== Ecriture et lecture ==" -ForegroundColor Cyan
$seed = @(
    @{ title = 'Actualite du jour'; views = 120; online = $true; tags = @('actu') },
    @{ title = 'Guide technique'; views = 45; online = $true; tags = @('tech', 'actu') },
    @{ title = 'Brouillon'; views = 0; online = $false; tags = @() }
)
foreach ($post in $seed) {
    Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body ($post | ConvertTo-Json -Depth 5) | Out-Null
}

$all = Invoke-RestMethod $rec -Headers $adminHeaders
Assert 'les trois enregistrements sont lus' ($all.totalItems -eq 3)

Write-Host "`n== Langage de filtre ==" -ForegroundColor Cyan
Assert 'comparaison numerique' ((Invoke-RestMethod "$rec`?filter=$(Encode 'views > 50')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'contient, insensible a la casse' ((Invoke-RestMethod "$rec`?filter=$(Encode "title ~ 'GUIDE'")" -Headers $adminHeaders).totalItems -eq 1)
Assert 'au moins un element (?=)' ((Invoke-RestMethod "$rec`?filter=$(Encode "tags ?= 'tech'")" -Headers $adminHeaders).totalItems -eq 1)
Assert 'conjonction' ((Invoke-RestMethod "$rec`?filter=$(Encode 'online = true && views > 100')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'booleen' ((Invoke-RestMethod "$rec`?filter=$(Encode 'online = false')" -Headers $adminHeaders).totalItems -eq 1)
Assert 'un champ hors schema est refuse' ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode 'secret = 1')" -Headers $adminHeaders }) -eq 400)
Assert 'une injection ne parse pas' ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode "title = 'a'; DROP TABLE posts;--")" -Headers $adminHeaders }) -eq 400)
Assert 'le tri hors schema est refuse' ((StatusOf { Invoke-RestMethod "$rec`?sort=-secret" -Headers $adminHeaders }) -eq 400)

$sorted = Invoke-RestMethod "$rec`?sort=-views&fields=title,views&skipTotal=1" -Headers $adminHeaders
Assert 'le tri decroissant est applique' ($sorted.items[0].views -eq 120)
Assert 'la projection retire les champs' ($null -eq $sorted.items[0].online)
Assert 'skipTotal evite le decompte' ($sorted.totalItems -eq -1)

Write-Host "`n== Dates automatiques ==" -ForegroundColor Cyan

# Le type autodate n'est pas reserve au moteur : une collection peut en declarer un.
$auto = @{
    name    = 'essai_autodate'; type = 'Base'
    fields  = @(
        @{ name = 'titre'; type = 'Text'; required = $true; options = @{} },
        @{ name = 'vu_le'; type = 'AutoDate'; options = @{ onCreate = $true; onUpdate = $true } },
        @{ name = 'cree_le'; type = 'AutoDate'; options = @{ onCreate = $true } }
    )
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}

$reste = try { Invoke-RestMethod "$api/collections/essai_autodate" -Headers $adminHeaders } catch { $null }
if ($reste) { Invoke-RestMethod "$api/collections/essai_autodate" -Method Delete -Headers $adminHeaders | Out-Null }

Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($auto | ConvertTo-Json -Depth 8) | Out-Null

# La date soumise est ecrasee : un champ dit automatique dont le client pose la valeur ne prouve
# plus rien sur le moment ou l'ecriture a eu lieu.
$ligne = Invoke-RestMethod "$api/collections/essai_autodate/records" -Method Post -Headers $adminHeaders `
    -Body (@{ titre = 'essai'; vu_le = '1999-01-01T00:00:00.000Z' } | ConvertTo-Json)

# ConvertFrom-Json rend des [datetime] pour les instants ISO : on compare des dates, pas des
# chaines, sinon on teste le format d'affichage de la machine.
Assert 'un autodate utilisateur est rempli a la creation' ([datetime]$ligne.vu_le -gt [datetime]'2020-01-01')
Assert 'la valeur soumise est ecrasee' (([datetime]$ligne.vu_le).Year -ne 1999)
Assert 'un autodate a la creation seule est rempli aussi' ([datetime]$ligne.cree_le -gt [datetime]'2020-01-01')

Start-Sleep -Milliseconds 1100
$modifie = Invoke-RestMethod "$api/collections/essai_autodate/records/$($ligne.id)" -Method Patch `
    -Headers $adminHeaders -Body (@{ titre = 'essai modifie' } | ConvertTo-Json)

Assert 'un autodate a la modification est rafraichi' ($modifie.vu_le -gt $ligne.vu_le)
# Celui qui n'est pose qu'a la creation ne doit pas bouger, sinon « cree le » ne veut rien dire.
Assert 'un autodate a la creation seule ne bouge pas' ($modifie.cree_le -eq $ligne.cree_le)

Invoke-RestMethod "$api/collections/essai_autodate" -Method Delete -Headers $adminHeaders | Out-Null

Write-Host "`n== Regles d'acces ==" -ForegroundColor Cyan
$restricted = $definition.Clone()
$restricted.rules = @{ list = 'online = true'; view = 'online = true'; create = ''; update = 'online = true'; delete = 'online = true' }
$restricted.fields = $collection.fields | Where-Object { -not $_.isSystem } | ForEach-Object {
    @{ id = $_.id; name = $_.name; type = $_.type; required = $_.required; maxSelect = $_.maxSelect; options = $_.options }
}
Invoke-RestMethod "$api/collections/posts" -Method Patch -Headers $adminHeaders -Body ($restricted | ConvertTo-Json -Depth 8) | Out-Null

Assert 'la regle filtre la liste' ((Invoke-RestMethod $rec -Headers $anonHeaders).totalItems -eq 2)
Assert 'le superadmin traverse la regle' ((Invoke-RestMethod $rec -Headers $adminHeaders).totalItems -eq 3)

$draft = (Invoke-RestMethod $rec -Headers $adminHeaders).items | Where-Object { $_.title -eq 'Brouillon' }
Assert 'consultation hors perimetre : 404, pas 403' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Headers $anonHeaders }) -eq 404)
Assert 'modification hors perimetre refusee' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Method Patch -Headers $anonHeaders -Body '{"title":"pirate"}' }) -eq 404)
Assert 'suppression hors perimetre refusee' ((StatusOf { Invoke-RestMethod "$rec/$($draft.id)" -Method Delete -Headers $anonHeaders }) -eq 404)

# LE test : la ligne visee doit exister encore, et intacte.
$survivor = (Invoke-RestMethod $rec -Headers $adminHeaders).items | Where-Object { $_.id -eq $draft.id }
Assert 'la ligne hors perimetre survit' ($null -ne $survivor)
Assert 'la ligne hors perimetre est intacte' ($survivor.title -eq 'Brouillon')

Write-Host "`n== Validation et integrite ==" -ForegroundColor Cyan
Assert 'doublon sur index unique : 409' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"Brouillon"}' }) -eq 409)
Assert 'champ obligatoire manquant : 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"views":5}' }) -eq 400)
Assert 'valeur hors liste : 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"x","tags":["inexistant"]}' }) -eq 400)
Assert 'champ inconnu soumis : 400' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $adminHeaders -Body '{"title":"y","inexistant":1}' }) -eq 400)

Write-Host "`n== Evolution du schema ==" -ForegroundColor Cyan
$renamed = $restricted.Clone()
$renamed.rules = @{ list = ''; view = ''; update = ''; delete = '' }   # create absente = verrouillee
$renamed.indexes = @(@{ name = 'idx_posts_title'; fields = @('titre'); unique = $true })
$renamed.fields = @($restricted.fields | ForEach-Object {
        $copy = $_.Clone()
        if ($copy.name -eq 'title') { $copy.name = 'titre' }   # renommage, identifiant conserve
        $copy
    })
$renamed.fields += @{ name = 'resume'; type = 'Text'; required = $false; maxSelect = 1; options = @{} }

Invoke-RestMethod "$api/collections/posts" -Method Patch -Headers $adminHeaders -Body ($renamed | ConvertTo-Json -Depth 8) | Out-Null

$afterRename = Invoke-RestMethod $rec -Headers $adminHeaders
Assert 'le renommage preserve les lignes' ($afterRename.totalItems -eq 3)
Assert 'le renommage preserve les valeurs' (($afterRename.items.titre | Where-Object { $_ -eq 'Brouillon' }).Count -eq 1)
Assert 'le champ ajoute est present' ($afterRename.items[0].PSObject.Properties.Name -contains 'resume')
Assert 'le filtre suit le nouveau nom' ((Invoke-RestMethod "$rec`?filter=$(Encode "titre ~ 'guide'")" -Headers $adminHeaders).totalItems -eq 1)
Assert "l'ancien nom n'existe plus" ((StatusOf { Invoke-RestMethod "$rec`?filter=$(Encode "title = 'x'")" -Headers $adminHeaders }) -eq 400)
Assert 'regle verrouillee (null) : 403' ((StatusOf { Invoke-RestMethod $rec -Method Post -Headers $anonHeaders -Body '{"titre":"tentative"}' }) -eq 403)

Write-Host "`n== Comptes locaux et RBAC ==" -ForegroundColor Cyan
$members = @{
    name    = 'members'; type = 'Auth'
    fields  = @(@{ name = 'displayName'; type = 'Text'; required = $false; maxSelect = 1; options = @{} })
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}
$old = try { Invoke-RestMethod "$api/collections/members" -Headers $adminHeaders } catch { $null }
if ($old) { Invoke-RestMethod "$api/collections/members" -Method Delete -Headers $adminHeaders | Out-Null }
Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($members | ConvertTo-Json -Depth 8) | Out-Null

$signup = @{ email = 'membre@exemple.fr'; password = 'motdepasse-solide'; password_confirm = 'motdepasse-solide'; displayName = 'Membre' }
$account = Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders -Body ($signup | ConvertTo-Json)
Assert 'un compte peut etre cree' ($account.email -eq 'membre@exemple.fr')
Assert 'le condensat ne sort pas a la creation' ($null -eq $account.password)
Assert 'le compte ne se verifie pas tout seul' ($account.verified -eq $false)
Assert "le compte n'a aucune permission a l'inscription" ($account.permissions.Count -eq 0)

Assert 'un mot de passe trop court est refuse' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
                -Body (@{ email = 'court@exemple.fr'; password = 'court' } | ConvertTo-Json)
        }) -eq 400)
Assert 'une confirmation discordante est refusee' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
                -Body (@{ email = 'x@exemple.fr'; password = 'motdepasse-solide'; password_confirm = 'autre-chose' } | ConvertTo-Json)
        }) -eq 400)

# LE test d'elevation de privileges : s'accorder des droits en s'inscrivant.
$escalation = Invoke-RestMethod "$api/collections/members/records" -Method Post -Headers $anonHeaders `
    -Body (@{ email = 'pirate@exemple.fr'; password = 'motdepasse-solide'; permissions = @('*'); roles = @('admin') } | ConvertTo-Json)
Assert "l'auto-attribution de permissions est ignoree" ($escalation.permissions.Count -eq 0)
Assert "l'auto-attribution de roles est ignoree" ($escalation.roles.Count -eq 0)

$memberSession = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = 'membre@exemple.fr'; password = 'motdepasse-solide' } | ConvertTo-Json)
$memberHeaders = @{ 'Authorization' = "Bearer $($memberSession.token)"; 'Content-Type' = 'application/json' }
Assert 'le compte peut se connecter' ($memberSession.token.Length -gt 20)
Assert "le compte n'est pas superadmin" ((Invoke-RestMethod "$api/me" -Headers $memberHeaders).isSuperuser -eq $false)
Assert "le compte n'administre pas les collections" ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $memberHeaders }) -eq 403)

# Rôle, puis attribution par le superadmin.
# Le rôle est retiré s'il subsiste d'une exécution précédente : la suite doit pouvoir être relancée
# sur une base déjà peuplée, sinon elle n'est utilisable qu'une fois.
$existingRole = (Invoke-RestMethod "$api/collections/_roles/records?filter=$(Encode "name = 'editeur'")" -Headers $adminHeaders).items
foreach ($role in $existingRole) {
    Invoke-RestMethod "$api/collections/_roles/records/$($role.id)" -Method Delete -Headers $adminHeaders | Out-Null
}

Invoke-RestMethod "$api/collections/_roles/records" -Method Post -Headers $adminHeaders `
    -Body (@{ name = 'editeur'; grants = @('posts.write', 'posts.publish') } | ConvertTo-Json) | Out-Null
Invoke-RestMethod "$api/collections/members/records/$($account.id)/grants" -Method Post -Headers $adminHeaders `
    -Body (@{ roles = @('editeur'); permissions = @('media.upload') } | ConvertTo-Json) | Out-Null

Assert "l'attribution revoque les sessions du compte" ((StatusOf { Invoke-RestMethod "$api/me" -Headers $memberHeaders }) -eq 401)

$memberSession = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = 'membre@exemple.fr'; password = 'motdepasse-solide' } | ConvertTo-Json)
$memberHeaders = @{ 'Authorization' = "Bearer $($memberSession.token)"; 'Content-Type' = 'application/json' }
$identity = Invoke-RestMethod "$api/me" -Headers $memberHeaders
Assert 'les permissions du role sont resolues' ($identity.permissions -contains 'posts.write')
Assert 'les derogations individuelles sont fusionnees' ($identity.permissions -contains 'media.upload')
Assert "un role non attribue n'accorde rien" (-not ($identity.permissions -contains 'users.manage'))

Assert "un compte ne peut pas s'attribuer de droits" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/records/$($account.id)/grants" -Method Post `
                -Headers $memberHeaders -Body (@{ roles = @(); permissions = @('*') } | ConvertTo-Json)
        }) -eq 403)

Write-Host "`n== Double authentification ==" -ForegroundColor Cyan
$methods = Invoke-RestMethod "$api/collections/members/auth-methods" -Headers $anonHeaders
Assert 'le mot de passe est une methode' ($methods.password -eq $true)
Assert 'aucun fournisseur externe non configure' ($methods.oauth2.Count -eq 0)
Assert "un fournisseur inconnu est refuse" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-oauth2" -Method Post -Headers $anonHeaders `
                -Body (@{ provider = 'inexistant'; code = 'x'; redirectUrl = 'http://localhost' } | ConvertTo-Json)
        }) -eq 400)

$enrollment = Invoke-RestMethod "$api/collections/members/mfa/enroll" -Method Post -Headers $memberHeaders
Assert 'un secret est produit' ($enrollment.secret.Length -eq 32)
Assert "l'uri d'inscription est exploitable" ($enrollment.uri -like 'otpauth://totp/*algorithm=SHA1*')

Assert 'un code faux ne confirme pas' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/mfa/confirm" -Method Post -Headers $memberHeaders `
                -Body (@{ code = '000000' } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/collections/members/mfa/confirm" -Method Post -Headers $memberHeaders `
    -Body (@{ code = Get-TotpCode $enrollment.secret } | ConvertTo-Json) | Out-Null

# LE test : le mot de passe seul ne doit plus rendre de jeton.
$firstFactor = $null
$challengeStatus = try {
    Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post -Headers $anonHeaders `
        -Body (@{ identity = 'membre@exemple.fr'; password = 'motdepasse-solide' } | ConvertTo-Json) | Out-Null
    200
}
catch {
    $firstFactor = $_.ErrorDetails.Message | ConvertFrom-Json
    $_.Exception.Response.StatusCode.value__
}

Assert 'le mot de passe seul ne suffit plus' ($challengeStatus -eq 401)
Assert 'un defi est ouvert' ($null -ne $firstFactor.mfaId)
Assert "aucun jeton n'est emis au premier facteur" ($null -eq $firstFactor.token)

Assert 'un second facteur faux est refuse' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
                -Body (@{ mfaId = $firstFactor.mfaId; code = '000000' } | ConvertTo-Json)
        }) -eq 400)

$secondFactor = Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
    -Body (@{ mfaId = $firstFactor.mfaId; code = (Get-TotpCode $enrollment.secret) } | ConvertTo-Json)
Assert 'le second facteur ouvre la session' ($secondFactor.token.Length -gt 20)

# Un défi rejouable transformerait une interception unique en accès permanent.
Assert "le defi n'est pas rejouable" ((StatusOf {
            Invoke-RestMethod "$api/collections/members/auth-with-otp" -Method Post -Headers $anonHeaders `
                -Body (@{ mfaId = $firstFactor.mfaId; code = (Get-TotpCode $enrollment.secret) } | ConvertTo-Json)
        }) -eq 400)

$mfaHeaders = @{ 'Authorization' = "Bearer $($secondFactor.token)"; 'Content-Type' = 'application/json' }
Assert 'la desactivation exige un code valide' ((StatusOf {
            Invoke-RestMethod "$api/collections/members/mfa/disable" -Method Post -Headers $mfaHeaders `
                -Body (@{ code = '000000' } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/collections/members/mfa/disable" -Method Post -Headers $mfaHeaders `
    -Body (@{ code = Get-TotpCode $enrollment.secret } | ConvertTo-Json) | Out-Null

$restored = Invoke-RestMethod "$api/collections/members/auth-with-password" -Method Post -Headers $anonHeaders `
    -Body (@{ identity = 'membre@exemple.fr'; password = 'motdepasse-solide' } | ConvertTo-Json)
Assert 'apres desactivation le mot de passe suffit a nouveau' ($restored.token.Length -gt 20)

Write-Host "`n== Fichiers ==" -ForegroundColor Cyan
$bearer = @{ Authorization = "Bearer $($session.token)" }
$documents = @{
    name    = 'documents'; type = 'Base'
    fields  = @(
        @{ name = 'titre'; type = 'Text'; required = $true; maxSelect = 1; options = @{} },
        @{ name = 'image'; type = 'File'; required = $false; maxSelect = 1; options = @{ maxFileSize = 2000000 } },
        @{ name = 'pieces'; type = 'File'; required = $false; maxSelect = 5; options = @{} }
    )
    indexes = @()
    rules   = @{ list = ''; view = ''; create = ''; update = ''; delete = '' }
}
try { Invoke-RestMethod "$api/collections/documents" -Method Delete -Headers $adminHeaders | Out-Null } catch {}
Invoke-RestMethod "$api/collections" -Method Post -Headers $adminHeaders -Body ($documents | ConvertTo-Json -Depth 8) | Out-Null

# Une vraie image, pour que la génération de vignette ait quelque chose à décoder.
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
    -Form @{ titre = 'Rapport'; image = Get-Item $source }
Assert 'un fichier est importe' ($document.image -match '\.png$')
Assert "le nom recoit un suffixe aleatoire" ($document.image -ne 'cratebase-smoke.png')

$fileUrl = "$api/files/documents/$($document.id)/$($document.image)"
$served = Invoke-WebRequest $fileUrl -UseBasicParsing
Assert 'le fichier est servi' ($served.StatusCode -eq 200)
Assert 'le type MIME est correct' ($served.Headers['Content-Type'] -contains 'image/png')

Assert 'une vignette est produite' ((Invoke-WebRequest "$fileUrl`?thumb=100x100" -UseBasicParsing).StatusCode -eq 200)
Assert 'une vignette a ratio libre est produite' ((Invoke-WebRequest "$fileUrl`?thumb=0x60" -UseBasicParsing).StatusCode -eq 200)
Assert 'une vignette demesuree est refusee' ((StatusOf { Invoke-WebRequest "$fileUrl`?thumb=99999x99999" -UseBasicParsing }) -eq 400)
Assert 'une traversee de repertoire est refusee' ((StatusOf { Invoke-WebRequest "$api/files/documents/$($document.id)/..%2F..%2Fcratebase.db" -UseBasicParsing }) -eq 404)

$folder = Invoke-RestMethod "$api/collections/documents/records" -Method Post -Headers $bearer `
    -Form @{ titre = 'Dossier'; pieces = @((Get-Item $copies[0]), (Get-Item $copies[1])) }
Assert 'plusieurs fichiers sont importes' ($folder.pieces.Count -eq 2)

$appended = Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Patch `
    -Headers $bearer -Form @{ 'pieces+' = Get-Item $copies[2] }
Assert "le modificateur '+' ajoute sans remplacer" ($appended.pieces.Count -eq 3)

$trimmed = Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Patch `
    -Headers $bearer -Form @{ 'pieces-' = $appended.pieces[0] }
Assert "le modificateur '-' retire un seul fichier" ($trimmed.pieces.Count -eq 2)

$oversize = Join-Path $env:TEMP 'cratebase-smoke-big.bin'
[IO.File]::WriteAllBytes($oversize, (New-Object byte[] 3000000))
Assert 'un fichier hors limite est refuse' ((StatusOf {
            Invoke-RestMethod "$api/collections/documents/records" -Method Post -Headers $bearer `
                -Form @{ titre = 'Trop gros'; image = Get-Item $oversize }
        }) -eq 400)

# Les octets suivent la ligne : sans cela, on facture et on sauvegarde des fichiers que plus rien
# ne désigne.
#
# Vérifié par l'API et non par le disque : le test doit valoir aussi bien pour le stockage local
# que pour S3, sans quoi la moitié de la promesse d'évolutivité resterait non couverte.
$orphanUrl = "$api/files/documents/$($folder.id)/$($trimmed.pieces[0])"
Assert 'les octets sont servis avant suppression' ((StatusOf { Invoke-WebRequest $orphanUrl -UseBasicParsing }) -eq 200)
Invoke-RestMethod "$api/collections/documents/records/$($folder.id)" -Method Delete -Headers $adminHeaders | Out-Null
Assert 'la suppression emporte les octets' ((StatusOf { Invoke-WebRequest $orphanUrl -UseBasicParsing }) -eq 404)

Remove-Item $source, $oversize -ErrorAction SilentlyContinue
$copies | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }

Write-Host "`n== Revocation ==" -ForegroundColor Cyan
$temporary = Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = $Email; password = $Password } | ConvertTo-Json)
$temporaryHeaders = @{ 'Authorization' = "Bearer $($temporary.token)"; 'Content-Type' = 'application/json' }

Assert 'le second jeton fonctionne' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $temporaryHeaders }) -eq 200)
Invoke-RestMethod "$api/collections/_superusers/auth-logout" -Method Post -Headers $temporaryHeaders | Out-Null
# Ce que les jetons opaques achetent, et qu'un JWT auto-signe ne peut pas offrir.
Assert 'la deconnexion revoque reellement le jeton' ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $temporaryHeaders }) -eq 401)
Assert "le jeton d'origine reste valide" ((StatusOf { Invoke-RestMethod "$api/me" -Headers $adminHeaders }) -eq 200)

Write-Host "`n== Super-admins ==" -ForegroundColor Cyan

# Le compte d'amorcage est le seul super-admin : le supprimer rendrait l'instance
# inadministrable, puisque toutes les collections systeme sont verrouillees et que l'amorcage ne
# recree un compte que si la configuration en porte un.
$self = Invoke-RestMethod "$api/me" -Headers $adminHeaders
Assert 'le dernier super-admin ne peut pas etre supprime' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records/$($self.id)" -Method Delete -Headers $adminHeaders
        }) -eq 409)
Assert 'le compte est toujours la' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $adminHeaders }) -eq 200)

$secondEmail = "second-$([Guid]::NewGuid().ToString('N').Substring(0, 8))@cratebase.local"
$second = Invoke-RestMethod "$api/collections/_superusers/records" -Method Post -Headers $adminHeaders `
    -Body (@{ email = $secondEmail; password = 'motdepasse-initial'; password_confirm = 'motdepasse-initial' } | ConvertTo-Json)

Assert 'un second super-admin se cree' ($null -ne $second.id)

$secondSession = Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
    -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'motdepasse-initial' } | ConvertTo-Json)
$secondHeaders = @{ 'Authorization' = "Bearer $($secondSession.token)"; 'Content-Type' = 'application/json' }

Assert 'le second compte administre' ((StatusOf { Invoke-RestMethod "$api/collections" -Headers $secondHeaders }) -eq 200)

# Changer un mot de passe doit fermer les sessions ouvertes : sans cela, changer son mot de passe
# apres un vol de session ne deconnecte pas le voleur, alors que c'est le premier reflexe de
# l'utilisateur — et il croirait le probleme regle.
Invoke-RestMethod "$api/collections/_superusers/records/$($second.id)" -Method Patch -Headers $adminHeaders `
    -Body (@{ password = 'motdepasse-remplace'; password_confirm = 'motdepasse-remplace' } | ConvertTo-Json) | Out-Null

Assert 'le changement de mot de passe revoque les sessions' ((StatusOf { Invoke-RestMethod "$api/me" -Headers $secondHeaders }) -eq 401)
Assert "l'ancien mot de passe ne vaut plus rien" ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'motdepasse-initial' } | ConvertTo-Json)
        }) -eq 400)
Assert 'le nouveau mot de passe ouvre une session' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/auth-with-password" -Method Post `
                -Headers $anonHeaders -Body (@{ identity = $secondEmail; password = 'motdepasse-remplace' } | ConvertTo-Json)
        }) -eq 200)

# Tant qu'il en reste deux, la suppression est permise : la garde porte sur le dernier, pas sur
# n'importe lequel.
Assert 'un super-admin sur deux se supprime' ((StatusOf {
            Invoke-RestMethod "$api/collections/_superusers/records/$($second.id)" -Method Delete -Headers $adminHeaders
        }) -eq 200)

Write-Host "`n== Journaux et reglages ==" -ForegroundColor Cyan

Assert 'le journal est refuse a un anonyme' ((StatusOf { Invoke-RestMethod "$api/logs" -Headers $anonHeaders }) -eq 401)
Assert "l'etat de l'instance est refuse a un anonyme" ((StatusOf { Invoke-RestMethod "$api/instance" -Headers $anonHeaders }) -eq 401)
Assert 'les reglages sont refuses a un anonyme' ((StatusOf { Invoke-RestMethod "$api/settings" -Headers $anonHeaders }) -eq 401)

$journal = Invoke-RestMethod "$api/logs?perPage=200&stats=1&granularity=Hour" -Headers $adminHeaders
$histogramme = ($journal.stats.items | Measure-Object -Property count -Sum).Sum

# Page et histogramme sortent du meme appel : deux appels videraient chacun le tampon d'ecriture,
# et le graphique annoncerait un total que le tableau sous lui ne montre pas.
Assert 'page et histogramme comptent pareil' ($journal.totalItems -eq $histogramme) "($($journal.totalItems) / $histogramme)"
Assert 'la suite a bien ete journalisee' ($journal.totalItems -gt 0)

$refus = Invoke-RestMethod "$api/logs?level=Warning&perPage=200" -Headers $adminHeaders
Assert 'les refus sont journalises en avertissement' (($refus.items | Where-Object { $_.status -eq 401 }).Count -gt 0)

# Le filtre de niveau retient un ensemble, pas une borne basse : demander les avertissements seuls
# ne doit ramener aucune erreur, sans quoi isoler les 4xx des vraies pannes redevient impossible.
Assert 'un niveau seul exclut les autres' (($refus.items | Where-Object { $_.level -ne 'Warning' }).Count -eq 0)
$graves = Invoke-RestMethod "$api/logs?level=Warning,Error&perPage=200" -Headers $adminHeaders
Assert 'plusieurs niveaux se cumulent' ($graves.totalItems -ge $refus.totalItems)
Assert 'aucun niveau hors de l ensemble demande' (($graves.items | Where-Object { $_.level -notin @('Warning', 'Error') }).Count -eq 0)
Assert 'aucune lecture du journal ne se journalise elle-meme' (($journal.items | Where-Object { $_.url -like '/api/logs*' }).Count -eq 0)

# Le jeton de fichier circule dans l'URL : sa duree de vie est de deux minutes, mais l'ecrire en
# clair dans une table conservee plusieurs jours annulerait la precaution.
$secret = "jeton-a-ne-pas-journaliser-$([Guid]::NewGuid().ToString('N'))"
StatusOf { Invoke-WebRequest "$api/files/documents/inexistant/absent.png?token=$secret" -UseBasicParsing } | Out-Null
Start-Sleep -Milliseconds 200
$fuite = Invoke-RestMethod "$api/logs?q=$(Encode $secret)" -Headers $adminHeaders
Assert 'le jeton de fichier est masque dans le journal' ($fuite.totalItems -eq 0)
$masque = Invoke-RestMethod "$api/logs?q=absent.png" -Headers $adminHeaders
Assert "le reste de l'URL est conserve" (($masque.items | Where-Object { $_.url -like '*token=***' }).Count -gt 0)

$reglages = Invoke-RestMethod "$api/settings" -Headers $adminHeaders
Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ appName = 'Cratebase — recette' } | ConvertTo-Json) | Out-Null
$apres = Invoke-RestMethod "$api/settings" -Headers $adminHeaders

# Un PATCH partiel ne doit rien remettre par defaut : un ecran qui n'envoie que le nom
# reinitialiserait sinon la retention et la collecte d'adresse, sans le moindre message.
Assert 'le PATCH partiel ne reinitialise pas les voisins' (
    $apres.appName -eq 'Cratebase — recette' -and
    $apres.logs.retentionDays -eq $reglages.logs.retentionDays -and
    $apres.logs.logIp -eq $reglages.logs.logIp)

Assert 'une retention hors bornes est refusee' ((StatusOf {
            Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders -Body (@{ logs = @{ retentionDays = 900 } } | ConvertTo-Json)
        }) -eq 400)

Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ appName = $reglages.appName } | ConvertTo-Json) | Out-Null

Write-Host "`n== Temps reel ==" -ForegroundColor Cyan

$flux = Start-Sse "$api/realtime" "Bearer $($session.token)"
Start-Sleep -Milliseconds 900

$accueil = Read-Sse $flux
Assert 'le flux annonce un identifiant de client' ($accueil -match 'event: connect')

$clientId = if ($accueil -match '"clientId":"([^"]+)"') { $Matches[1] } else { '' }
Assert 'l identifiant de client est exploitable' ($clientId.Length -gt 10)

Invoke-RestMethod "$api/realtime" -Method Post -Headers $adminHeaders `
    -Body (@{ clientId = $clientId; subscriptions = @('posts') } | ConvertTo-Json) | Out-Null

$vivant = Invoke-RestMethod $rec -Method Post -Headers $adminHeaders `
    -Body (@{ titre = 'diffuse en direct'; views = 7; online = $true; tags = @() } | ConvertTo-Json -Depth 5)

Start-Sleep -Milliseconds 900
$recu = Read-Sse $flux

Assert 'une creation est diffusee' ($recu -match 'event: posts')
Assert 'l evenement porte l action' ($recu -match '"action":"create"')
# L'enregistrement entier voyage : sans lui, chaque abonne devrait relire la ligne, et cent
# abonnes produiraient cent lectures par ecriture.
Assert 'l evenement porte l enregistrement' ($recu -match 'diffuse en direct')

Invoke-RestMethod "$rec/$($vivant.id)" -Method Delete -Headers $adminHeaders | Out-Null
Start-Sleep -Milliseconds 700
Assert 'une suppression est diffusee' ((Read-Sse $flux) -match '"action":"delete"')

Stop-Sse $flux

# Le point qui decide de la valeur du reste : s'abonner ne doit pas contourner les regles d'acces.
# La collection des super-admins est verrouillee ; un anonyme qui s'y abonne ne doit rien recevoir.
$anonyme = Start-Sse "$api/realtime" ''
Start-Sleep -Milliseconds 900
$bienvenue = Read-Sse $anonyme
$anonId = if ($bienvenue -match '"clientId":"([^"]+)"') { $Matches[1] } else { '' }

Invoke-RestMethod "$api/realtime" -Method Post -Headers $anonHeaders `
    -Body (@{ clientId = $anonId; subscriptions = @('_superusers', 'posts') } | ConvertTo-Json) | Out-Null

$fantome = Invoke-RestMethod "$api/collections/_superusers/records" -Method Post -Headers $adminHeaders `
    -Body (@{ email = "fantome-$([Guid]::NewGuid().ToString('N').Substring(0,8))@exemple.fr"
              password = 'motdepasse-solide'; password_confirm = 'motdepasse-solide' } | ConvertTo-Json)

Start-Sleep -Milliseconds 900
$vu = Read-Sse $anonyme

Assert 'un anonyme ne recoit rien d une collection verrouillee' ($vu -notmatch 'event: _superusers')

Invoke-RestMethod "$api/collections/_superusers/records/$($fantome.id)" -Method Delete -Headers $adminHeaders | Out-Null
Stop-Sse $anonyme

Assert 'le temps reel se coupe depuis les reglages' ((Invoke-RestMethod "$api/settings" -Method Patch `
            -Headers $adminHeaders -Body (@{ realtime = @{ enabled = $false } } | ConvertTo-Json -Depth 5)
    ).realtime.enabled -eq $false)

Assert 'un flux est refuse quand le temps reel est ferme' ((StatusOf {
            Invoke-RestMethod "$api/realtime" -Headers $adminHeaders
        }) -eq 400)

Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
    -Body (@{ realtime = @{ enabled = $true } } | ConvertTo-Json -Depth 5) | Out-Null

Assert 'un nombre de flux hors bornes est refuse' ((StatusOf {
            Invoke-RestMethod "$api/settings" -Method Patch -Headers $adminHeaders `
                -Body (@{ realtime = @{ maxClients = 0 } } | ConvertTo-Json -Depth 5)
        }) -eq 400)

Write-Host "`n== Stockage ==" -ForegroundColor Cyan

$magasin = Invoke-RestMethod "$api/storage" -Headers $adminHeaders
Assert 'le magasin se decrit' ($magasin.kind -in @('local', 's3'))

# Aucun chemin ne doit ramener une cle secrete : la console peut lire la configuration, jamais la
# valeur qui permettrait de s'en servir ailleurs.
Assert 'aucune cle secrete ne sort du processus' (
    -not ($magasin.PSObject.Properties.Name -contains 'secretKey') -and
    -not ($magasin.PSObject.Properties.Name -contains 'accessKey'))

$objets = Invoke-RestMethod "$api/storage/objects?perPage=200" -Headers $adminHeaders
Assert 'l inventaire repond' ($objets.totalItems -ge 0)

# Le fichier importe plus haut est encore reference par son enregistrement : il ne doit donc pas
# etre compte comme orphelin, sinon la detection ne vaut rien.
$vivant = $objets.items | Where-Object { $_.fileName -eq $document.image -and -not $_.isThumb }
if ($vivant) {
    Assert 'un fichier reference n est pas orphelin' (-not $vivant.orphan)

    # Et il ne doit pas etre supprimable depuis l inventaire : l enlever laisserait
    # l enregistrement pointer vers rien.
    Assert 'un fichier reference ne se supprime pas d ici' ((StatusOf {
                Invoke-RestMethod "$api/storage/objects" -Method Delete -Headers $adminHeaders `
                    -Body (@{ keys = @($vivant.key) } | ConvertTo-Json)
            }) -eq 409)
}

$vignettes = Invoke-RestMethod "$api/storage/objects?kind=thumbs&perPage=200" -Headers $adminHeaders
Assert 'le filtre de nature ne ramene que des vignettes' (
    ($vignettes.items | Where-Object { -not $_.isThumb }).Count -eq 0)

$occupation = Invoke-RestMethod "$api/usage" -Headers $adminHeaders
Assert 'la base se mesure elle-meme' ($occupation.database.bytes -gt 0)
Assert 'le moteur est nomme' ($occupation.database.engine -in @('sqlite', 'postgres'))
Assert 'les fichiers sont totalises' ($occupation.files.bytes -ge 0 -and $occupation.files.objects -ge 0)

# Une capacite a zero signifie « inconnue » et non « nulle » : ni PostgreSQL ni S3 n'exposent de
# limite de facon portable, et l'ecran doit alors montrer un volume sans jauge.
Assert 'une capacite absente vaut zero, pas une invention' (
    $occupation.database.capacityBytes -ge 0 -and $occupation.files.capacityBytes -ge 0)

if ($occupation.host.available) {
    Assert 'le volume hote est mesure' ($occupation.host.totalBytes -gt 0)
    Assert 'l espace libre tient dans le total' ($occupation.host.freeBytes -le $occupation.host.totalBytes)
}

$sonde = Invoke-RestMethod "$api/storage/check" -Method Post -Headers $adminHeaders
Assert 'le magasin passe le test de bout en bout' ($sonde.ok)
Assert 'le test ecrit, relit et supprime' (($sonde.steps | Where-Object { $_.state -eq 'ok' }).Count -ge 4)

$archive = Join-Path ([IO.Path]::GetTempPath()) "cratebase-smoke-$([Guid]::NewGuid().ToString('N')).zip"
$jeton = (Invoke-RestMethod "$api/files/token" -Method Post -Headers $adminHeaders).token
Invoke-WebRequest "$api/storage/archive?token=$jeton" -OutFile $archive -UseBasicParsing | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$entrees = $zip.Entries.Count
$vignettesArchivees = ($zip.Entries | Where-Object { $_.FullName -like '*/thumbs_*' }).Count
$zip.Dispose()
Remove-Item $archive -Force

Assert 'l archive contient des fichiers' ($entrees -gt 0)
# Les vignettes se regenerent : les archiver reviendrait a archiver un cache, et a en doubler le poids.
Assert 'l archive exclut les vignettes' ($vignettesArchivees -eq 0)

Assert 'l archive exige une identite' ((StatusOf { Invoke-WebRequest "$api/storage/archive" -UseBasicParsing }) -eq 401)

Write-Host "`n---------------------------------------------" -ForegroundColor Cyan
Write-Host "  $script:passed reussis, $script:failed echoues" -ForegroundColor $(if ($script:failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "---------------------------------------------`n" -ForegroundColor Cyan

exit $(if ($script:failed -eq 0) { 0 } else { 1 })
