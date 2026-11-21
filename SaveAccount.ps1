# Credentials are copied as bytes; errors and UI never reveal their content.
. (Join-Path $PSScriptRoot 'UiStyle.ps1')

function Assert-SavePlainPath {
    param([string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $entry = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($entry -and (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw '不支持重解析点。'
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if (-not $parent) { break }
        $cursor = $parent.FullName
    }
}

function Save-SwitchAccount {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Root, [AllowEmptyString()][string]$Name)
    $lock = $null
    $source = $null
    $output = $null
    $temporary = $null
    $result = [pscustomobject]@{ Success = $false; Message = '保存失败，请检查本机账号目录的访问权限。' }
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -cne $Name.Trim() -or
        $Name -match '[<>:"/\\|?*\x00-\x1f]' -or $Name -match '[. ]$' -or
        $Name.StartsWith('.') -or $Name -match '^backup-' -or $Name -match '_auth$' -or
        $Name -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)') {
        $result.Message = '名称无效，请使用不含路径字符、备份前缀或 _auth 后缀的普通名称。'
        return $result
    }
    try {
        $fullRoot = [IO.Path]::GetFullPath($Root)
        $auth = Join-Path $fullRoot 'auth.json'
        $accounts = Join-Path $fullRoot 'accounts'
        Assert-SavePlainPath $auth
        Assert-SavePlainPath $accounts
        if (-not [IO.File]::Exists($auth)) {
            $result.Message = '没有可保存的当前登录，请先登录 Codex。'
            return $result
        }
        [void][IO.Directory]::CreateDirectory($accounts)
        Assert-SavePlainPath $accounts
        $lockPath = Join-Path $accounts '.switch.lock'
        Assert-SavePlainPath $lockPath
        try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch { $result.Message = '账号目录正忙或不可写，请稍后重试。'; return $result }
        $destination = Join-Path $accounts ($Name + '_auth.json')
        $legacy = Join-Path $accounts ($Name + '.json')
        foreach ($path in @($destination, $legacy)) {
            Assert-SavePlainPath $path
            if (Test-Path -LiteralPath $path) {
                $result.Message = '同名账号已经存在，请换一个名称。'
                return $result
            }
        }
        # Share Read allows readers while preventing concurrent write/delete during this snapshot.
        $source = [IO.File]::Open($auth, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($source.Length -eq 0) {
            $result.Message = '当前登录文件为空，未保存账号。'
            return $result
        }
        $temporary = Join-Path $accounts ('.save-' + [guid]::NewGuid().ToString('N') + '.tmp')
        $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $source.CopyTo($output)
        $output.Flush($true)
        $output.Dispose()
        $output = $null
        $source.Dispose()
        $source = $null
        Assert-SavePlainPath $accounts
        foreach ($path in @($destination, $legacy)) {
            Assert-SavePlainPath $path
            if (Test-Path -LiteralPath $path) {
                $result.Message = '同名账号已经存在，请换一个名称。'
                return $result
            }
        }
        # Same-directory move publishes a complete file and never overwrites an existing destination.
        [IO.File]::Move($temporary, $destination)
        $temporary = $null
        $result.Success = $true
        $result.Message = '已保存账号“{0}”。' -f $Name
        return $result
    } catch {
        return $result
    } finally {
        if ($output) { $output.Dispose() }
        if ($source) { $source.Dispose() }
        if ($temporary) { try { [IO.File]::Delete($temporary) } catch { } }
        if ($lock) { $lock.Dispose() }
    }
}

function Show-SaveAccountNameDialog {
    param($Owner, [Parameter(Mandatory = $true)][scriptblock]$SaveAction)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $palette = Get-SwitchPalette
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.SuspendLayout()
    try {
        $dialog.Name = 'SaveAccountDialog'
        $dialog.Text = '保存账号'
        $dialog.StartPosition = 'CenterParent'
        $dialog.FormBorderStyle = 'FixedDialog'
        $dialog.AutoScaleMode = 'Dpi'
        $dialog.AutoScaleDimensions = New-Object System.Drawing.SizeF(96, 96)
        $dialog.ClientSize = New-Object System.Drawing.Size(440, 304)
        $dialog.MaximizeBox = $false
        $dialog.MinimizeBox = $false
        $dialog.ShowIcon = $false
        $dialog.ShowInTaskbar = $false
        $dialog.BackColor = $palette.Surface
        $dialog.ForeColor = $palette.Ink
        $dialog.Font = $Owner.Font
        $title = New-Object System.Windows.Forms.Label
        $title.Text = '保存当前登录'
        $title.Font = New-Object System.Drawing.Font($Owner.Font.FontFamily, 15, [System.Drawing.FontStyle]::Bold)
        $title.AutoSize = $true
        $title.Location = New-Object System.Drawing.Point(24, 20)
        $hint = New-Object System.Windows.Forms.Label
        $hint.Text = '为当前登录添加名称，方便下次选择。'
        $hint.AutoSize = $true
        $hint.ForeColor = $palette.Muted
        $hint.Location = New-Object System.Drawing.Point(24, 61)
        $label = New-Object System.Windows.Forms.Label
        $label.Text = '账号名称'
        $label.AutoSize = $true
        $label.Location = New-Object System.Drawing.Point(24, 100)
        $nameBox = New-Object System.Windows.Forms.TextBox
        $nameBox.Name = 'AccountNameInput'
        $nameBox.AccessibleName = '账号名称'
        $nameBox.Location = New-Object System.Drawing.Point(24, 127)
        $nameBox.Size = New-Object System.Drawing.Size(392, 30)
        $nameBox.BorderStyle = 'FixedSingle'
        $nameBox.MaxLength = 100
        $nameBox.TabIndex = 0
        $errorLabel = New-Object System.Windows.Forms.Label
        $errorLabel.Name = 'SaveErrorLabel'
        $errorLabel.AccessibleName = '保存提示'
        $errorLabel.ForeColor = $palette.Error
        $errorLabel.Location = New-Object System.Drawing.Point(24, 169)
        $errorLabel.Size = New-Object System.Drawing.Size(392, 52)
        $errorLabel.Padding = New-Object Windows.Forms.Padding(7, 4, 7, 4)
        $ok = New-Object System.Windows.Forms.Button
        $ok.Name = 'ConfirmSaveButton'
        $ok.Text = '保存账号'
        $ok.Location = New-Object System.Drawing.Point(308, 240)
        $ok.Size = New-Object System.Drawing.Size(108, 40)
        Set-SwitchButtonStyle -Button $ok -Role Primary
        $ok.TabIndex = 1
        # Do not assign OK here: failed backend validation must keep this dialog open.
        $ok.DialogResult = [System.Windows.Forms.DialogResult]::None
        $cancel = New-Object System.Windows.Forms.Button
        $cancel.Name = 'CancelSaveButton'
        $cancel.Text = '取消'
        $cancel.Location = New-Object System.Drawing.Point(208, 240)
        $cancel.Size = New-Object System.Drawing.Size(88, 40)
        Set-SwitchButtonStyle -Button $cancel -Role Secondary
        $cancel.TabIndex = 2
        $cancel.CausesValidation = $false
        $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $nameBox.Add_TextChanged({
            $errorLabel.Text = ''
            Set-SwitchInputError -TextBox $nameBox -HasError $false
        }.GetNewClosure())
        $errorLabel.Add_TextChanged({
            $errorLabel.BackColor = if ($errorLabel.Text) { $palette.ErrorSurface } else { $palette.Surface }
        }.GetNewClosure())
        $ok.Add_Click({
            $ok.Enabled = $false
            try {
                # All name and filesystem rules remain in the existing save backend.
                $result = & $SaveAction -Name $nameBox.Text
                if ($result.Success) { $dialog.DialogResult = [System.Windows.Forms.DialogResult]::OK }
                else {
                    $errorLabel.Text = $result.Message
                    Set-SwitchInputError -TextBox $nameBox -HasError $true
                    [void]$nameBox.Focus()
                }
            } catch {
                $errorLabel.Text = '保存失败，请检查本机账号目录的访问权限。'
                Set-SwitchInputError -TextBox $nameBox -HasError $true
                [void]$nameBox.Focus()
            } finally { $ok.Enabled = $true }
        }.GetNewClosure())
        $dialog.Add_Shown({ [void]$nameBox.Focus() }.GetNewClosure())
        $dialog.Controls.AddRange(@($title, $hint, $label, $nameBox, $errorLabel, $ok, $cancel))
        Set-SwitchInputStyle -TextBox $nameBox
        $dialog.AcceptButton = $ok
        $dialog.CancelButton = $cancel
        $dialog.ResumeLayout($false)
        $dialog.PerformLayout()
        if ($dialog.ShowDialog($Owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $nameBox.Text }
        return $null
    } finally {
        $titleFont = $title.Font
        $dialog.Dispose()
        if ($titleFont) { $titleFont.Dispose() }
    }
}

function Connect-SaveAccount {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Window, [Parameter(Mandatory = $true)][string]$Root)
    $showName = (Get-Command Show-SaveAccountNameDialog).ScriptBlock
    $save = (Get-Command Save-SwitchAccount).ScriptBlock
    $handler = {
        $Window.SaveButton.Enabled = $false
        try {
            $state = @{ Result = $null }
            $saveBackend = $save
            $saveRoot = $Root
            $saveAction = {
                param([AllowEmptyString()][string]$Name)
                $state.Result = & $saveBackend -Root $saveRoot -Name $Name
                return $state.Result
            }.GetNewClosure()
            $name = & $showName -Owner $Window.Form -SaveAction $saveAction
            if ($null -eq $name) { $Window.StatusLabel.Text = '已取消保存。'; return }
            if ($state.Result.Success) { & $Window.RefreshAccounts -SelectName $name }
            $Window.StatusLabel.Text = $state.Result.Message
        } catch {
            $Window.StatusLabel.Text = '保存失败，请检查本机账号目录的访问权限。'
        } finally {
            $Window.SaveButton.Enabled = $true
            [void]$Window.SaveButton.Focus()
        }
    }.GetNewClosure()
    $Window.SaveButton.Add_Click($handler)
    $Window.SaveButton.Enabled = $true
}
