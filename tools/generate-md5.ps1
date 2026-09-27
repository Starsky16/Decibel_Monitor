param($path)

Write-Host "处理路径: $path" -ForegroundColor Green

# 删除旧的 .md5sum 文件
Remove-Item "$path/*.md5sum" -ErrorAction SilentlyContinue
Remove-Item "$path/*.md" -ErrorAction SilentlyContinue

$files = Get-ChildItem $path
$hashes = [ordered]@{}
$summary = @"
> [!important]
> 下载时请注意核对文件MD5是否正确。

| 文件名 | MD5 |
| --- | --- |
"@
# here-string 不含结尾换行，不补的话第一行数据会与表头折行粘连，表格在 Release 正文里渲染错乱
$summary += "`n"

foreach ($i in $files) {
    $name = $i.Name
    $hash = Get-FileHash $i.FullName -Algorithm MD5
    $hashString = $hash.Hash
    $hashes.Add($name, $hashString)
    
    # 生成单独的 .md5sum 文件（修复1：使用 Set-Content）
    $hash.Hash | Set-Content "$($i.FullName).md5sum"
    
    $summary += "| $name | ``${hashString}`` |`n"
}

Write-Host "已计算 $($hashes.Count) 个文件的哈希值" -ForegroundColor Green

$json = ConvertTo-Json $hashes -Compress
$summary += "`n<!-- CLASSISLAND_PKG_MD5 ${json} -->"

# 写入汇总文件（修复2：使用 Out-File）
$summary | Out-File "$path/checksums.md" -Encoding UTF8

Write-Host "MD5 汇总已生成:" -ForegroundColor Gray
Write-Host $summary -ForegroundColor Gray
Write-Host "----------" -ForegroundColor Gray
