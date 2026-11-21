param([string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }))

. (Join-Path $PSScriptRoot 'AccountList.ps1')
. (Join-Path $PSScriptRoot 'AccountSwitch.ps1')
. (Join-Path $PSScriptRoot 'SaveAccount.ps1')
. (Join-Path $PSScriptRoot 'UiStyle.ps1')

function New-SwitchWindow {
    [CmdletBinding()]
    param(
        [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }),
        [scriptblock]$SwitchAction
    )
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $palette = Get-SwitchPalette
    $ink = $palette.Ink
    $muted = $palette.Muted
    $sidebarColor = $palette.Sidebar
    $border = $palette.Border
    $form = New-Object Windows.Forms.Form
    $form.Name = 'CodexSwitchWindow'
    $form.Text = 'Codex 账号切换'
    $form.ShowIcon = $false
    $form.StartPosition = 'CenterScreen'
    $form.ClientSize = New-Object Drawing.Size(860, 550)
    $form.MinimumSize = New-Object Drawing.Size(780, 550)
    $form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10)
    $form.ForeColor = $ink
    $form.BackColor = $palette.Surface
    $form.AutoScaleMode = 'Font'

    $layout = New-Object Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'
    $layout.ColumnCount = 2
    $layout.RowCount = 1
    $layout.Margin = 0
    [void]$layout.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Absolute', 244)))
    [void]$layout.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Percent', 100)))
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Percent', 100)))
    $form.Controls.Add($layout)
    $sidebar = New-Object Windows.Forms.Panel
    $sidebar.Dock = 'Fill'
    $sidebar.Margin = 0
    $sidebar.BackColor = $sidebarColor
    $layout.Controls.Add($sidebar, 0, 0)
    $sidebarLine = New-Object Windows.Forms.Panel
    $sidebarLine.Dock = 'Right'
    $sidebarLine.Width = 1
    $sidebarLine.BackColor = $border
    $sidebar.Controls.Add($sidebarLine)

    function New-WindowLabel {
        param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width, [int]$Height, [single]$FontSize = 10, [switch]$Bold, [switch]$Dimmed)
        $label = New-Object Windows.Forms.Label
        $label.Text = $Text
        $label.Location = New-Object Drawing.Point($X, $Y)
        $label.Size = New-Object Drawing.Size($Width, $Height)
        $style = if ($Bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
        $label.Font = New-Object Drawing.Font('Microsoft YaHei UI', $FontSize, $style)
        $label.ForeColor = if ($Dimmed) { $muted } else { $ink }
        $label.UseMnemonic = $false
        $Parent.Controls.Add($label)
        return $label
    }
    [void](New-WindowLabel $sidebar 'Codex Switch' 23 29 200 35 15 -Bold)
    [void](New-WindowLabel $sidebar '搜索账号' 23 87 198 22 9 -Dimmed)
    $searchBox = New-Object Windows.Forms.TextBox
    $searchBox.Name = 'SearchBox'
    $searchBox.AccessibleName = '搜索账号名称'
    $searchBox.Location = New-Object Drawing.Point(23, 112)
    $searchBox.Size = New-Object Drawing.Size(198, 30)
    $searchBox.BorderStyle = 'FixedSingle'
    $searchBox.TabIndex = 0
    $sidebar.Controls.Add($searchBox)
    Set-SwitchInputStyle -TextBox $searchBox
    $countLabel = New-WindowLabel $sidebar '已保存的账号' 23 165 198 24 9 -Dimmed
    $countLabel.Name = 'CountLabel'

    # Keep a native ListBox: owner drawing changes spacing and colors only.
    $accountList = New-Object Windows.Forms.ListBox
    $accountList.Name = 'AccountList'
    $accountList.AccessibleName = '已保存的账号'
    $accountList.Location = New-Object Drawing.Point(16, 196)
    $accountList.Size = New-Object Drawing.Size(212, 258)
    $accountList.Anchor = 'Top, Bottom, Left, Right'
    $accountList.BorderStyle = 'None'
    $accountList.BackColor = $sidebarColor
    $accountList.IntegralHeight = $false
    $accountList.DrawMode = 'OwnerDrawFixed'
    $accountList.ItemHeight = 44
    $accountList.TabIndex = 1
    $sidebar.Controls.Add($accountList)
    $drawItem = {
        param($listControl, $e)
        if ($e.Index -lt 0 -or $e.Index -ge $listControl.Items.Count) { return }
        $selected = (($e.State -band [Windows.Forms.DrawItemState]::Selected) -ne 0)
        $itemColors = Get-SwitchPalette
        $background = if ($selected) { $itemColors.Selection } else { $itemColors.Sidebar }
        $foreground = if ($selected) { $itemColors.SelectionInk } else { $itemColors.Ink }
        if (-not $listControl.Enabled) { $foreground = [Drawing.SystemColors]::GrayText }
        $brush = New-Object Drawing.SolidBrush($background)
        try { $e.Graphics.FillRectangle($brush, $e.Bounds) } finally { $brush.Dispose() }
        if ($selected) {
            $marker = New-Object Drawing.SolidBrush($itemColors.Primary)
            try { $e.Graphics.FillRectangle($marker, $e.Bounds.X, ($e.Bounds.Y + 8), 3, ($e.Bounds.Height - 16)) } finally { $marker.Dispose() }
            $outline = New-Object Drawing.Pen($itemColors.Border)
            try { $e.Graphics.DrawRectangle($outline, $e.Bounds.X, $e.Bounds.Y, ($e.Bounds.Width - 1), ($e.Bounds.Height - 1)) } finally { $outline.Dispose() }
        }
        $textBounds = New-Object Drawing.Rectangle(($e.Bounds.X + 12), $e.Bounds.Y, ([Math]::Max(0, $e.Bounds.Width - 24)), $e.Bounds.Height)
        $flags = [Windows.Forms.TextFormatFlags]'Left, VerticalCenter, SingleLine, EndEllipsis, NoPrefix'
        [Windows.Forms.TextRenderer]::DrawText($e.Graphics, [string]$listControl.Items[$e.Index], $listControl.Font, $textBounds, $foreground, $flags)
        $e.DrawFocusRectangle()
    }.GetNewClosure()
    $accountList.Add_DrawItem($drawItem)
    $saveButton = New-Object Windows.Forms.Button
    $saveButton.Name = 'SaveButton'
    $saveButton.Text = '保存当前登录'
    $saveButton.Location = New-Object Drawing.Point(23, 482)
    $saveButton.Size = New-Object Drawing.Size(198, 43)
    $saveButton.Anchor = 'Bottom, Left, Right'
    Set-SwitchButtonStyle -Button $saveButton -Role Secondary
    $saveButton.TabIndex = 2
    $sidebar.Controls.Add($saveButton)

    $detail = New-Object Windows.Forms.Panel
    $detail.Dock = 'Fill'
    $detail.Margin = 0
    $layout.Controls.Add($detail, 1, 0)
    [void](New-WindowLabel $detail '切换账号' 34 25 400 48 23 -Bold)
    $subheading = New-WindowLabel $detail '在列表中选中账号，按 Enter 也可切换。' 38 86 540 36 10 -Dimmed
    $subheading.Anchor = 'Top, Left, Right'
    [void](New-WindowLabel $detail '目标账号' 38 140 300 24 9 -Dimmed)
    $detailName = New-WindowLabel $detail '' 34 171 544 50 20 -Bold
    $detailName.Name = 'DetailNameLabel'
    $detailName.AccessibleName = '目标账号名称'
    $detailName.Anchor = 'Top, Left, Right'
    $detailName.AutoEllipsis = $true
    $nameTip = New-Object Windows.Forms.ToolTip
    $nameTip.ShowAlways = $true
    $divider = New-Object Windows.Forms.Panel
    $divider.Location = New-Object Drawing.Point(38, 241)
    $divider.Size = New-Object Drawing.Size(540, 1)
    $divider.Anchor = 'Top, Left, Right'
    $divider.BackColor = $border
    $detail.Controls.Add($divider)
    $detailHint = New-WindowLabel $detail '' 38 263 540 60 10 -Dimmed
    $detailHint.Name = 'DetailHintLabel'
    $detailHint.Anchor = 'Top, Left, Right'
    $switchButton = New-Object Windows.Forms.Button
    $switchButton.Name = 'SwitchButton'
    $switchButton.Text = '切换到此账号'
    $switchButton.AccessibleName = '切换到所选账号'
    $switchButton.Location = New-Object Drawing.Point(38, 343)
    $switchButton.Size = New-Object Drawing.Size(188, 45)
    Set-SwitchButtonStyle -Button $switchButton -Role Primary
    $switchButton.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10, [Drawing.FontStyle]::Bold)
    $switchButton.TabIndex = 0
    $switchButton.Enabled = $false
    $detail.Controls.Add($switchButton)
    $statusLabel = New-WindowLabel $detail '' 38 407 540 76 10 -Dimmed
    $statusLabel.Name = 'StatusLabel'
    $statusLabel.AccessibleName = '操作状态'
    $statusLabel.Anchor = 'Top, Left, Right'
    $statusLabel.Padding = New-Object Windows.Forms.Padding(12, 10, 12, 10)
    $statusLabel.AutoEllipsis = $true
    $statusLabel.Add_TextChanged({
        $statusLabel.BackColor = if ($statusLabel.Text) { $palette.Sidebar } else { $palette.Surface }
        $nameTip.SetToolTip($statusLabel, $statusLabel.Text)
    }.GetNewClosure())

    if (-not $SwitchAction) {
        $SwitchAction = { param($Root, $Name) Invoke-DesktopAccountSwitch -Root $Root -Name $Name -Owner $form }.GetNewClosure()
    }
    $state = @{ Action = $SwitchAction; Busy = $false; Names = @(); Loading = $false; ReadFailed = $false }
    $updateDetail = {
        $hasSelection = ($null -ne $accountList.SelectedItem)
        $switchButton.Enabled = (-not $state.Busy -and $hasSelection)
        if ($hasSelection) {
            $detailName.Text = [string]$accountList.SelectedItem
            $detailHint.Text = '若 Codex 桌面版正在运行，会先询问是否退出，切换后重新打开。'
            $nameTip.SetToolTip($detailName, [string]$accountList.SelectedItem)
        } else {
            $detailName.Text = if ($state.ReadFailed) { '暂时无法读取账号' } elseif ($state.Names.Count -eq 0) { '还没有已保存的账号' } else { '没有匹配的账号' }
            $detailHint.Text = if ($state.ReadFailed) { '请检查本机账号目录的访问权限。' } elseif ($state.Names.Count -eq 0) { '先登录 Codex，再使用左侧“保存当前登录”添加账号。' } else { '试试其他名称，或清空搜索。' }
            $nameTip.SetToolTip($detailName, '')
        }
    }.GetNewClosure()
    $renderAccounts = {
        param([string]$SelectName)
        $selected = if ($SelectName) { $SelectName } else { [string]$accountList.SelectedItem }
        $query = $searchBox.Text.Trim()
        $accountList.BeginUpdate()
        $state.Loading = $true
        try {
            $accountList.Items.Clear()
            foreach ($name in $state.Names) {
                if (-not $query -or $name.IndexOf($query, [StringComparison]::OrdinalIgnoreCase) -ge 0) { [void]$accountList.Items.Add($name) }
            }
            if ($selected -and $accountList.Items.Contains($selected)) { $accountList.SelectedItem = $selected }
            elseif ($accountList.Items.Count -gt 0) { $accountList.SelectedIndex = 0 }
            $countLabel.Text = if ($state.ReadFailed) { '账号列表不可用' } elseif ($query) { '筛选结果 · {0} / {1}' -f $accountList.Items.Count, $state.Names.Count } elseif ($state.Names.Count -eq 0) { '还没有已保存的账号。' } else { '已保存 {0} 个账号。' -f $state.Names.Count }
        } finally {
            $state.Loading = $false
            $accountList.EndUpdate()
            & $updateDetail
        }
    }.GetNewClosure()
    $readNames = (Get-Command Get-SwitchAccountNames).ScriptBlock
    $refreshAccounts = {
        param([string]$SelectName)
        try { $state.Names = @(& $readNames -Root $Root); $state.ReadFailed = $false }
        catch { $state.Names = @(); $state.ReadFailed = $true }
        if ($SelectName) {
            $state.Loading = $true
            try { $searchBox.Clear() } finally { $state.Loading = $false }
        }
        & $renderAccounts -SelectName $SelectName
        # Refresh updates the catalog only. Keep the last real save/switch result.
    }.GetNewClosure()
    $invokeSelected = {
        if ($state.Busy -or $null -eq $accountList.SelectedItem) { return }
        # Capture only the current native selection after filtering has completed.
        $target = [string]$accountList.SelectedItem
        $state.Busy = $true
        $switchButton.Enabled = $false
        $accountList.Enabled = $false
        $searchBox.Enabled = $false
        $saveButton.Enabled = $false
        $statusLabel.Text = '正在切换，请稍候…'
        try {
            $result = & $state.Action -Root $Root -Name $target
            $statusLabel.Text = $result.Message
            if ($result.Code -in @('RestartFailed','DesktopFailed','DesktopUnknown','RecoveryRequired')) {
                [void](Show-SwitchNotice -Owner $form -Title '切换结果' -Message $result.Message -Mode Result)
            }
        } catch { $statusLabel.Text = '切换未完成，请检查本机账号目录。' }
        finally {
            $state.Busy = $false
            $accountList.Enabled = $true
            $searchBox.Enabled = $true
            $saveButton.Enabled = $true
            & $updateDetail
        }
    }.GetNewClosure()
    $setAction = { param([scriptblock]$Action) $state.Action = $Action }.GetNewClosure()
    $selectionChanged = { if (-not $state.Loading) { & $updateDetail } }.GetNewClosure()
    $searchChanged = { if (-not $state.Loading) { & $renderAccounts } }.GetNewClosure()
    $keyDown = {
        param($listControl, $eventArgs)
        if ($eventArgs.KeyCode -eq [Windows.Forms.Keys]::Enter) {
            $eventArgs.SuppressKeyPress = $true
            & $invokeSelected
        }
    }.GetNewClosure()
    $accountList.Add_SelectedIndexChanged($selectionChanged)
    $accountList.Add_KeyDown($keyDown)
    $searchBox.Add_TextChanged($searchChanged)
    $switchButton.Add_Click($invokeSelected)
    $form.Add_Disposed({ $nameTip.Dispose() }.GetNewClosure())
    & $refreshAccounts
    $window = [pscustomobject]@{
        Form = $form
        AccountList = $accountList
        StatusLabel = $statusLabel
        SwitchButton = $switchButton
        SaveButton = $saveButton
        SearchBox = $searchBox
        CountLabel = $countLabel
        DetailNameLabel = $detailName
        DetailHintLabel = $detailHint
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
