param([string] $BaseUrl = 'http://127.0.0.1:53110')
$ErrorActionPreference = 'Stop'
$checks = 0
function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Send([string] $Method, [string] $Path, $Body = $null, [string] $Token = '', [int] $Status = 200) {
    $arguments = @{ Method=$Method; Uri="$BaseUrl/api$Path"; SkipHttpErrorCheck=$true }
    if ($Token) { $arguments.Headers = @{ Authorization="Token $Token" } }
    if ($null -ne $Body) {
        $arguments.Body = $Body | ConvertTo-Json -Depth 10 -Compress
        $arguments.ContentType = 'application/json; charset=utf-8'
    }
    $response = Invoke-WebRequest @arguments
    Assert ($response.StatusCode -eq $Status) "$Method $Path returned $($response.StatusCode): $($response.Content)"
    if ($response.Content) { return $response.Content | ConvertFrom-Json -DateKind String }
}
$suffix = [Guid]::NewGuid().ToString('N')
$author = (Send POST '/users' @{ user=@{ username="author-$suffix"; email="author-$suffix@example.test"; password='edge-test-password' } } -Status 201).user
$viewer = (Send POST '/users' @{ user=@{ username="viewer-$suffix"; email="viewer-$suffix@example.test"; password='edge-test-password' } } -Status 201).user
$tag = "edge-$suffix"
$slugs = [System.Collections.Generic.List[string]]::new()
try {
    foreach ($tags in @(@("z-$tag", "a-$tag"), @())) {
        $article = (Send POST '/articles' @{ article=@{ title="Edge $($slugs.Count) $suffix"; description='edge'; body='body'; tagList=$tags } } $author.token 201).article
        $slugs.Add($article.slug)
        Assert ($article.body -eq 'body') 'Article detail must retain the body.'
        Assert ($article.createdAt -match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$') 'Timestamp must have exactly three UTC fractional digits.'
        Assert (($article.tagList -join ',') -eq (($tags | Sort-Object) -join ',')) 'Tags must be sorted, including the empty-array case.'
    }
    $empty = Send GET "/articles/$($slugs[0])/comments"
    Assert ($empty.comments.Count -eq 0) 'An existing article without comments must return an empty array.'
    $missing = Send GET "/articles/missing-$suffix/comments" -Status 404
    Assert ($missing.errors.body.Count -gt 0) 'Missing articles must retain the RealWorld error envelope.'
    Send POST "/profiles/$($author.username)/follow" -Token $viewer.token | Out-Null
    $feed = Send GET '/articles/feed?limit=1&offset=1' -Token $viewer.token
    Assert ($feed.articles.Count -eq 1 -and $feed.articles[0].slug -eq $slugs[0] -and $feed.articlesCount -eq 2) 'Bounded feed pagination must return the correct second article and total.'
    Assert $feed.articles[0].author.following 'Feed authors must be followed by the viewer.'
    Assert (-not $feed.articles[0].PSObject.Properties['body']) 'Feed lists must omit the body.'
    $overflow = Send GET '/articles/feed?limit=100&offset=2147483647' -Token $viewer.token
    Assert ($overflow.articles.Count -eq 0 -and $overflow.articlesCount -eq 2) 'Feed pagination must not overflow when offset + limit exceeds Int32.MaxValue.'
    $filtered = Send GET "/articles?tag=a-$tag&author=missing-$suffix&favorited=missing-$suffix"
    Assert ($filtered.articles.Count -eq 1 -and $filtered.articlesCount -eq 1 -and $filtered.articles[0].slug -eq $slugs[0]) 'Tag filtering must retain precedence over author and favorited filters.'
    Send POST "/articles/missing-$suffix/favorite" -Token $viewer.token -Status 404 | Out-Null
    Send DELETE "/articles/missing-$suffix/favorite" -Token $viewer.token -Status 404 | Out-Null
    $unchanged = (Send DELETE "/articles/$($slugs[1])/favorite" -Token $viewer.token).article
    Assert (-not $unchanged.favorited -and $unchanged.favoritesCount -eq 0) 'Unfavoriting an existing, unfavorited article must succeed without changing its count.'
    $favoriteUrl = "$BaseUrl/api/articles/$($slugs[0])/favorite"
    $headers = @{ Authorization="Token $($viewer.token)" }
    $statuses = 1..8 | ForEach-Object -Parallel {
        (Invoke-WebRequest -Method POST -Uri $using:favoriteUrl -Headers $using:headers -SkipHttpErrorCheck).StatusCode
    }
    Assert (@($statuses | Where-Object { $_ -ne 200 }).Count -eq 0) 'Concurrent favorite inserts must all succeed.'
    $favorites = Send GET "/articles?favorited=$($viewer.username)" -Token $viewer.token
    Assert ($favorites.articles.Count -eq 1 -and $favorites.articlesCount -eq 1 -and $favorites.articles[0].favorited -and $favorites.articles[0].favoritesCount -eq 1) 'Concurrent upserts must create exactly one favorite and retain viewer-specific state.'
    $anonymous = (Send GET "/articles/$($slugs[0])").article
    Assert (-not $anonymous.favorited -and -not $anonymous.author.following) 'Pooled contexts and compiled queries must not leak viewer-specific state.'
    foreach ($attempt in 1..2) {
        $removed = (Send DELETE "/articles/$($slugs[0])/favorite" -Token $viewer.token).article
        Assert (-not $removed.favorited -and $removed.favoritesCount -eq 0) 'Repeated unfavorite requests must be idempotent and return the updated article.'
    }
    $createUrl = "$BaseUrl/api/articles"
    $headers = @{ Authorization="Token $($author.token)" }
    $responses = 1..2 | ForEach-Object -Parallel {
        $names = @("shared-$using:suffix", "other-$using:suffix")
        if ($_ -eq 2) { [Array]::Reverse($names) }
        $body = @{ article=@{ title="Shared $_ $using:suffix"; description='edge'; body='body'; tagList=$names } } | ConvertTo-Json -Depth 6
        $response = Invoke-WebRequest -Method POST -Uri $using:createUrl -Headers $using:headers -Body $body -ContentType 'application/json' -SkipHttpErrorCheck
        [pscustomobject]@{ Status=$response.StatusCode; Content=$response.Content }
    }
    foreach ($response in $responses) {
        Assert ($response.Status -eq 201) "Concurrent tag creation failed: $($response.Content)"
        $created = ($response.Content | ConvertFrom-Json -DateKind String).article
        $slugs.Add($created.slug)
        Assert ($created.tagList.Count -eq 2) 'Concurrent tag upserts must preserve both tags.'
    }
    Write-Output "$checks focused API assertions passed."
}
finally {
    foreach ($slug in $slugs) { Send DELETE "/articles/$slug" -Token $author.token -Status 204 | Out-Null }
}
