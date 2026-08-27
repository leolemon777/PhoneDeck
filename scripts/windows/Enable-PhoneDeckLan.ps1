#Requires -RunAsAdministrator

$ruleName = 'PhoneDeck Secure LAN (TCP 8766)'
$existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

if ($null -eq $existingRule) {
    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort 8766 `
        -Profile Private,Public `
        -RemoteAddress LocalSubnet | Out-Null
    Write-Host 'PhoneDeck Wi-Fi 防火墙规则已创建（仅本地子网，TCP 8766）。'
} else {
    Set-NetFirewallRule -DisplayName $ruleName -Enabled True -Profile Private,Public
    $existingRule | Get-NetFirewallAddressFilter |
        Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
    Write-Host 'PhoneDeck Wi-Fi 防火墙规则已存在并已启用。'
}

Write-Host '规则同时适用于专用/公用网络，但只接受本地子网来源。'
