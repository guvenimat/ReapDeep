$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$reco = "http://localhost:8002"
$tag = "rc" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0
function Id([string]$name) { "$name-$tag" }
$A = Id "shirt"; $B = Id "tee"; $C = Id "chinos"; $D = Id "dress"; $E = Id "boots"; $F = Id "scarf"

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = New-Object System.Net.CookieContainer
$session = New-Object System.Net.Http.HttpClient($handler)
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$url, $body = $null, [hashtable]$headers = @{}) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($null -ne $body) {
        $text = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Depth 10 -Compress }
        $req.Content = New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
    }
    $r = $client.SendAsync($req).Result
    $t = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, $body = $null) { Send $session $method "$api$path" $body @{ "X-Project-Id" = "1" } }
function K([string]$key, [string]$method, [string]$path, $body = $null) { Send $plain $method "$api$path" $body @{ Authorization = "Bearer $key" } }
function Reco([string]$query) { Send $plain GET "$reco/recommend?api_key=pk_dev_local&$query" }
function Ids($response) { return ,@($response.Json.items | ForEach-Object { $_.item_id }) }
function Capture([string]$batchJson) {
    $content = New-Object System.Net.Http.StringContent(('{"api_key":"pk_dev_local","batch":' + $batchJson + '}'), [Text.Encoding]::UTF8, "text/plain")
    $r = $plain.PostAsync("http://localhost:8000/capture", $content).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture HTTP $([int]$r.StatusCode)" }
}
function Events([string]$distinctId, [string]$event, [string[]]$items) {
    ($items | ForEach-Object { "{`"event`":`"$event`",`"distinct_id`":`"$distinctId`",`"properties`":{`"item_id`":`"$_`"}}" }) -join ","
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 2 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((Send $session POST "$api/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Catalog (tag: $tag)"
$ck = (S POST "/api-keys" @{ name = "Catalog $tag"; scope = "catalog_write" }).Json
Check "catalog_write key is created with the ck_ prefix" ($ck.key -like "ck_*" -and $ck.api_key.scope -eq "catalog_write")
$items = @(
    @{ item_id = $A; title = "Shirt"; category = "apparel-$tag"; price = 49 },
    @{ item_id = $B; title = "Tee"; category = "apparel-$tag"; price = 19 },
    @{ item_id = $C; title = "Chinos"; category = "apparel-$tag"; price = 59 },
    @{ item_id = $D; title = "Dress"; category = "apparel-$tag"; price = 89; in_stock = $false },
    @{ item_id = $E; title = "Boots"; category = "shoes-$tag"; price = 129 },
    @{ item_id = $F; title = "Scarf"; category = "accessories-$tag"; price = 25 }
)
$up = K $ck.key PUT "/items" @{ items = $items }
Check "catalog_write key uploads 6 items" ($up.Status -eq 200 -and $up.Json.upserted -eq 6)
Check "read key cannot write the catalog -> 403" ((K $readKey PUT "/items" @{ items = @(@{ item_id = "x"; title = "x" }) }).Status -eq 403)
Check "catalog_write key cannot query analytics -> 403" ((K $ck.key POST "/query/trends" @{ series = @(@{ event = "x" }) }).Status -eq 403)
Check "catalog_write key can read the catalog" ((K $ck.key GET "/items?search=$tag").Json.total -ge 6)
Check "invalid item (no title) -> 400" ((K $ck.key PUT "/items" @{ items = @(@{ item_id = "x" }) }).Status -eq 400)

Write-Host "`n2) Interactions"
Capture ("[" + ((1..3 | ForEach-Object { Events "p$_-$tag" "product_viewed" @($A, $B) }) -join ",") + "]")
Capture ("[" + (Events "p4-$tag" "product_viewed" @($A, $D)) + "," + (Events "p5-$tag" "product_viewed" @($A, $D)) + "]")
Capture ("[" + (Events "anon-$tag" "product_viewed" @($A)) + "]")
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"`$identify`",`"distinct_id`":`"user-$tag`",`"properties`":{`"`$anon_distinct_id`":`"anon-$tag`"}}]"
Start-Sleep -Milliseconds 300
Capture ("[" + (Events "user-$tag" "product_viewed" @($C)) + "," + (Events "p6-$tag" "product_viewed" @($A, $C)) + "]")
Capture ("[" + (Events "b1-$tag" "purchase" @($A, $E)) + "," + (Events "b2-$tag" "purchase" @($A, $E)) + "]")
Capture ("[" + (Events "p7-$tag" "product_viewed" @($F)) + "," + (Events "p8-$tag" "product_viewed" @($F)) + "]")

$ready = WaitFor {
    $d = (S GET "/items/$A").Json
    $script:detail = $d
    @($d.co_viewed).Count -ge 3 -and @($d.bought_together).Count -ge 1 -and
        (Ids (Reco "model=popular&category=accessories-$tag")) -contains $F
} 180
Check "worker calculated co_viewed and bought_together for the new items" $ready
$co = @($detail.co_viewed | ForEach-Object { $_.item_id })
Check "co_viewed for shirt: tee first (3 people), then chinos and dress -> $($co -join ', ')" ($co[0] -eq $B -and $co -contains $C -and $co -contains $D)
Check "anonymous view before identify counts for the merged person (shirt-chinos needs both of them)" ($co -contains $C)
Check "bought_together for shirt: boots" (@($detail.bought_together | ForEach-Object { $_.item_id }) -contains $E)
$models = (S GET "/recommendations/models").Json.models
Check "all four models have a calculation" (@($models).Count -eq 4)

Write-Host "`n3) Public Recommender"
$served = WaitFor { (Ids (Reco "model=co_viewed&item_id=$A&limit=3"))[0] -eq $B } 30
$r = Reco "model=co_viewed&item_id=$A&limit=3"
$ids = Ids $r
Check "co_viewed: tee and chinos, never the out-of-stock dress or the item itself -> $($ids -join ', ')" ($served -and $ids[0] -eq $B -and $ids -contains $C -and $ids -notcontains $D -and $ids -notcontains $A)
$long = Reco "model=co_viewed&item_id=$A&limit=6"
Check "a short list is filled with popular items, marked as such -> $((Ids $long) -join ', ')" ((Ids $long) -contains $F -and @($long.Json.items | Where-Object { $_.item_id -eq $F })[0].source -eq "popular" -and $long.Json.items[0].source -eq "co_viewed")
Check "items carry catalog fields (title, price, category)" ($r.Json.items[0].title -eq "Tee" -and $r.Json.items[0].price -eq 19)
Check "bought_together: boots" ((Ids (Reco "model=bought_together&item_id=$A&limit=1")) -contains $E)
Check "category filter on popular" (@((Reco "model=popular&category=shoes-$tag").Json.items | Where-Object { $_.category -ne "shoes-$tag" }).Count -eq 0)
$null = K $ck.key PUT "/items" @{ items = @(@{ item_id = $B; title = "Tee"; category = "apparel-$tag"; price = 19; in_stock = $false }) }
$dropped = WaitFor { (Ids (Reco "model=co_viewed&item_id=$A&limit=3")) -notcontains $B } 30
Check "an item going out of stock disappears without recalculation" $dropped
Check "unknown public key -> 401" ((Send $plain GET "$reco/recommend?api_key=pk_nope").Status -eq 401)
Check "co_viewed without item_id -> 400" ((Reco "model=co_viewed").Status -eq 400)
Check "unknown model -> 400" ((Reco "model=collaborative").Status -eq 400)

Write-Host "`n4) Quality endpoint"
$q = S POST "/recommendations/evaluate?k=10"
Check "hit rate endpoint answers with counts and rates" ($q.Status -eq 200 -and $null -ne $q.Json.cases -and $q.Json.k -eq 10)
Check "read key cannot run the evaluation -> 403" ((K $readKey POST "/recommendations/evaluate").Status -eq 403)

foreach ($i in @($A, $B, $C, $D, $E, $F)) { $null = S DELETE "/items/$i" }
$null = S DELETE "/api-keys/$($ck.api_key.id)"
$left = (S GET "/items?search=$tag").Json
Check "test items deleted -> $(@($left.items).Count) left" (@($left.items).Count -eq 0)
Check "revoked catalog key -> 401" ((K $ck.key GET "/items").Status -eq 401)

if ($script:failed -eq 0) { Write-Host "`nPASSED: all recommender checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
