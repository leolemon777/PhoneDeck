#Requires -RunAsAdministrator

function Ensure-PhoneDeckFirewallRule {
    param(
        [string]$RuleName,
        [string]$Protocol,
        [int]$LocalPort
    )
    $existingRule = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if ($null -eq $existingRule) {
        New-NetFirewallRule `
            -DisplayName $RuleName `
            -Direction Inbound `
            -Action Allow `
            -Protocol $Protocol `
            -LocalPort $LocalPort `
            -Profile Private,Public `
            -RemoteAddress LocalSubnet | Out-Null
        Write-Host "PhoneDeck 防火墙规则已创建（仅本地子网，$Protocol $LocalPort）。"
    } else {
        Set-NetFirewallRule -DisplayName $RuleName -Enabled True -Profile Private,Public
        $existingRule | Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
        Write-Host "PhoneDeck 防火墙规则已存在并已启用（$Protocol $LocalPort）。"
    }
}

Ensure-PhoneDeckFirewallRule -RuleName 'PhoneDeck Secure LAN (TCP 8766)' -Protocol TCP -LocalPort 8766
Ensure-PhoneDeckFirewallRule -RuleName 'PhoneDeck Discovery (UDP 8767)' -Protocol UDP -LocalPort 8767
# 同一 Wi-Fi 免扫码连接：手机用 mDNS（_phonedeck._tcp）发现电脑。
Ensure-PhoneDeckFirewallRule -RuleName 'PhoneDeck mDNS (UDP 5353)' -Protocol UDP -LocalPort 5353

Write-Host '规则同时适用于专用/公用网络，但只接受本地子网来源。'
