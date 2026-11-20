# Credentials are copied as bytes; errors and UI never reveal their content.
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
    param($Owner)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $dialog = New-Object System.Windows.Forms.Form
    try {
        $dialog.Name = 'SaveAccountDialog'
        $dialog.Text = '保存当前登录'
        $dialog.StartPosition = 'CenterParent'
        $dialog.FormBorderStyle = 'FixedDialog'
        $dialog.ClientSize = New-Object System.Drawing.Size(420, 145)
        $dialog.MaximizeBox = $false
        $dialog.MinimizeBox = $false
        $dialog.Font = $Owner.Font
        $label = New-Object System.Windows.Forms.Label
        $label.Text = '为当前登录输入一个新的账号名称：'
        $label.AutoSize = $true
        $label.Location = New-Object System.Drawing.Point(16, 16)
        $input = New-Object System.Windows.Forms.TextBox
        $input.Name = 'AccountNameInput'
        $input.AccessibleName = '账号名称'
        $input.Location = New-Object System.Drawing.Point(16, 44)
        $input.Size = New-Object System.Drawing.Size(388, 28)
        $input.MaxLength = 100
        $ok = New-Object System.Windows.Forms.Button
        $ok.Name = 'ConfirmSaveButton'
        $ok.Text = '保存'
        $ok.Location = New-Object System.Drawing.Point(232, 94)
        $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $cancel = New-Object System.Windows.Forms.Button
        $cancel.Name = 'CancelSaveButton'
        $cancel.Text = '取消'
        $cancel.Location = New-Object System.Drawing.Point(320, 94)
        $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $dialog.Controls.AddRange(@($label, $input, $ok, $cancel))
        $dialog.AcceptButton = $ok
        $dialog.CancelButton = $cancel
        if ($dialog.ShowDialog($Owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $input.Text }
        return $null
    } finally { $dialog.Dispose() }
}

function Connect-SaveAccount {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Window, [Parameter(Mandatory = $true)][string]$Root)
    $showName = (Get-Command Show-SaveAccountNameDialog).ScriptBlock
    $save = (Get-Command Save-SwitchAccount).ScriptBlock
    $handler = {
        $Window.SaveButton.Enabled = $false
        try {
            $name = & $showName -Owner $Window.Form
            if ($null -eq $name) { $Window.StatusLabel.Text = '已取消保存。'; return }
            $result = & $save -Root $Root -Name $name
            if ($result.Success) { & $Window.RefreshAccounts }
            $Window.StatusLabel.Text = $result.Message
        } catch {
            $Window.StatusLabel.Text = '保存失败，请检查本机账号目录的访问权限。'
        } finally { $Window.SaveButton.Enabled = $true }
    }.GetNewClosure()
    $Window.SaveButton.Add_Click($handler)
    $Window.SaveButton.Enabled = $true
}
