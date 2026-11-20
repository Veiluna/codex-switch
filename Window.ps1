param([string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }))

. (Join-Path $PSScriptRoot 'AccountList.ps1')
. (Join-Path $PSScriptRoot 'AccountSwitch.ps1')
. (Join-Path $PSScriptRoot 'SaveAccount.ps1')

function New-SwitchWindow {
    [CmdletBinding()]
    param(
        [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }),
        [scriptblock]$SwitchAction
    )

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object System.Windows.Forms.Form
    $form.Name = 'CodexSwitchWindow'
    $form.Text = 'Codex 账号切换'
    $form.StartPosition = 'CenterScreen'
    $form.ClientSize = New-Object System.Drawing.Size(560, 390)
    $form.MinimumSize = New-Object System.Drawing.Size(480, 400)
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)

    $heading = New-Object System.Windows.Forms.Label
    $heading.Text = '已保存的账号'
    $heading.AutoSize = $true
    $heading.Location = New-Object System.Drawing.Point(24, 20)
    $form.Controls.Add($heading)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = '单击账号即可切换；也可选中后按 Enter 或使用切换按钮。'
    $hint.AutoSize = $true
    $hint.Location = New-Object System.Drawing.Point(24, 50)
    $form.Controls.Add($hint)

    $accountList = New-Object System.Windows.Forms.ListBox
    $accountList.Name = 'AccountList'
    $accountList.AccessibleName = '已保存的账号'
    $accountList.Location = New-Object System.Drawing.Point(24, 82)
    $accountList.Size = New-Object System.Drawing.Size(512, 190)
    $accountList.Anchor = 'Top, Bottom, Left, Right'
    $accountList.IntegralHeight = $false
    $form.Controls.Add($accountList)

    $statusLabel = New-Object System.Windows.Forms.Label
    $statusLabel.Name = 'StatusLabel'
    $statusLabel.Location = New-Object System.Drawing.Point(24, 285)
    $statusLabel.Size = New-Object System.Drawing.Size(512, 44)
    $statusLabel.Anchor = 'Bottom, Left, Right'
    $form.Controls.Add($statusLabel)

    $switchButton = New-Object System.Windows.Forms.Button
    $switchButton.Name = 'SwitchButton'
    $switchButton.Text = '切换到所选账号'
    $switchButton.Location = New-Object System.Drawing.Point(24, 330)
    $switchButton.Size = New-Object System.Drawing.Size(160, 36)
    $switchButton.Anchor = 'Bottom, Left'
    $switchButton.Enabled = $false
    $form.Controls.Add($switchButton)

    $saveButton = New-Object System.Windows.Forms.Button
    $saveButton.Name = 'SaveButton'
    $saveButton.Text = '保存当前登录'
    $saveButton.Location = New-Object System.Drawing.Point(200, 330)
    $saveButton.Size = New-Object System.Drawing.Size(160, 36)
    $saveButton.Anchor = 'Bottom, Left'
    $saveButton.Enabled = $false
    $form.Controls.Add($saveButton)

    $readNames = (Get-Command Get-SwitchAccountNames).ScriptBlock
    $refreshAccounts = {
        $selected = $accountList.SelectedItem
        $accountList.Items.Clear()
        try {
            $names = @(& $readNames -Root $Root)
            foreach ($name in $names) { [void]$accountList.Items.Add($name) }
            if ($selected -and $accountList.Items.Contains($selected)) {
                $accountList.SelectedItem = $selected
            }
            $statusLabel.Text = if ($names.Count -eq 0) { '还没有已保存的账号。' } else { '已保存 {0} 个账号。' -f $names.Count }
        } catch {
            $statusLabel.Text = '无法读取账号列表，请检查本机账号目录的访问权限。'
        }
    }.GetNewClosure()
    & $refreshAccounts

    # Mutable callback entry point for ticket 04; all UI triggers share this action.
    if (-not $SwitchAction) {
        $SwitchAction = { param($Root, $Name) Invoke-DesktopAccountSwitch -Root $Root -Name $Name -Owner $form }.GetNewClosure()
    }
    $switchState = @{ Action = $SwitchAction; Busy = $false }
    $invokeSelected = {
        if ($switchState.Busy -or $null -eq $accountList.SelectedItem) { return }
        $switchState.Busy = $true
        $switchButton.Enabled = $false
        try {
            $result = & $switchState.Action -Root $Root -Name ([string]$accountList.SelectedItem)
            $statusLabel.Text = $result.Message
            if ($result.Code -in @('RestartFailed','DesktopFailed','DesktopUnknown','RecoveryRequired')) {
                [void][System.Windows.Forms.MessageBox]::Show($form, $result.Message, '切换结果', 'OK', 'Information')
            }
        } catch { $statusLabel.Text = '切换未完成，请检查本机账号目录。' }
        finally {
            $switchState.Busy = $false
            $switchButton.Enabled = ($null -ne $accountList.SelectedItem)
        }
    }.GetNewClosure()
    $setAction = { param([scriptblock]$Action) $switchState.Action = $Action }.GetNewClosure()
    $selectionChanged = {
        $switchButton.Enabled = (-not $switchState.Busy -and $null -ne $accountList.SelectedItem)
    }.GetNewClosure()
    $accountList.Add_SelectedIndexChanged($selectionChanged)
    # Coordinates must hit a real row; empty space must not reuse selection.
    $mouseClick = {
        param($sender, $eventArgs)
        if ($eventArgs.Button -ne [System.Windows.Forms.MouseButtons]::Left) { return }
        $index = $accountList.IndexFromPoint($eventArgs.Location)
        if ($index -ge 0 -and $index -lt $accountList.Items.Count) {
            $accountList.SelectedIndex = $index
            & $invokeSelected
        }
    }.GetNewClosure()
    $keyDown = {
        param($sender, $eventArgs)
        if ($eventArgs.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
            $eventArgs.SuppressKeyPress = $true
            & $invokeSelected
        }
    }.GetNewClosure()
    $accountList.Add_MouseClick($mouseClick)
    $accountList.Add_KeyDown($keyDown)
    $switchButton.Add_Click($invokeSelected)

    $window = [pscustomobject]@{
        Form = $form
        AccountList = $accountList
        StatusLabel = $statusLabel
        SwitchButton = $switchButton
        SaveButton = $saveButton
        RefreshAccounts = $refreshAccounts
        InvokeSelectedAccount = $invokeSelected
        SetSwitchAction = $setAction
    }
    Connect-SaveAccount -Window $window -Root $Root
    return $window
}

if ($MyInvocation.InvocationName -ne '.') {
    $window = New-SwitchWindow -Root $Root
    try { [void]$window.Form.ShowDialog() }
    finally { $window.Form.Dispose() }
}
