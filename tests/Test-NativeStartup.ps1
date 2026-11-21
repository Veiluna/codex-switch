param([string]$ExecutablePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\CodexSwitch.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not [IO.File]::Exists($ExecutablePath)) { throw 'Build the native application before running this smoke test.' }
$temporary = Join-Path $PSScriptRoot ('.tmp-startup-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
function Get-ControlText($Control) {
    try { $value = $Control.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1); if ($value) { return $value } } catch {}
    try { $value = $Control.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value; if ($value) { return $value } } catch {}
    return $Control.Current.Name
}
try {
    foreach ($scenario in @('empty', 'saved', 'current', 'unknown', 'damaged')) {
        $fakeRoot = Join-Path $temporary $scenario
        $accounts = Join-Path $fakeRoot 'accounts'
        [void][IO.Directory]::CreateDirectory($accounts)
        $aliases = @('演示账号', '长名称账号用来验证原生详情显示')
        if ($scenario -ne 'empty') {
            for ($index = 0; $index -lt $aliases.Count; $index++) {
                $json = '{"tokens":{"account_id":"fixture-account-' + $index + '"}}'
                [IO.File]::WriteAllText((Join-Path $accounts ($aliases[$index] + '_auth.json')), $json, [Text.Encoding]::UTF8)
            }
            if ($scenario -eq 'current') { [IO.File]::Copy((Join-Path $accounts ($aliases[0] + '_auth.json')), (Join-Path $fakeRoot 'auth.json')) }
            if ($scenario -eq 'unknown') { [IO.File]::WriteAllText((Join-Path $fakeRoot 'auth.json'), '{"tokens":{"account_id":"unknown-current"}}', [Text.Encoding]::UTF8) }
            if ($scenario -eq 'damaged') { [IO.File]::WriteAllText((Join-Path $fakeRoot 'auth.json'), '{invalid-fixture', [Text.Encoding]::UTF8) }
        }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $ExecutablePath
        $start.WorkingDirectory = $fakeRoot
        $start.UseShellExecute = $false
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.EnvironmentVariables['CODEX_HOME'] = $fakeRoot
        $process = [Diagnostics.Process]::Start($start)
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(12)
            $healthySince = $null
            do {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
                if ($process.HasExited) { throw ('Native startup exited unexpectedly: ' + $scenario + ', code ' + $process.ExitCode) }
                # A .NET error dialog also owns a window handle. Check exact PID, title and actual app controls.
                $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
                $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
                $main = $null
                foreach ($window in $windows) {
                    if ($window.Current.ControlType -ne [Windows.Automation.ControlType]::Window) { continue }
                    if ($window.Current.Name -ne 'Codex 账号切换') { throw ('Unexpected startup dialog: ' + $window.Current.Name) }
                    $main = $window
                }
                if ($null -eq $main) { $healthySince = $null; continue }
                $elements = @($main.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
                # Framework 4.x uses a legacy MSAA proxy whose AutomationIds can be numeric handles.
                $search = $elements | Where-Object { $_.Current.ClassName -like 'WindowsForms10.EDIT.*' } | Select-Object -First 1
                $list = $elements | Where-Object { $_.Current.ClassName -like 'WindowsForms10.LISTBOX.*' } | Select-Object -First 1
                $switch = $elements | Where-Object { $_.Current.Name -eq '切换到此账号' -and $_.Current.ClassName -like 'WindowsForms10.BUTTON.*' } | Select-Object -First 1
                if ($null -eq $search -or $null -eq $list -or $null -eq $switch) { $healthySince = $null; continue }
                $details = @($elements | Where-Object { $_.Current.ClassName -like 'WindowsForms10.RichEdit*' })
                $detailText = (@($details | ForEach-Object { Get-ControlText $_ }) -join ' ')
                if ($scenario -eq 'empty' -and $detailText -notmatch '还没有已保存的账号') { throw 'Empty-account state was not rendered.' }
                if ($scenario -in @('saved','current','damaged') -and $detailText -notmatch '演示账号|长名称账号') { throw 'Selected saved-account state was not rendered.' }
                if ($scenario -eq 'unknown' -and $detailText -notmatch '当前登录（未保存）') { throw 'Unknown current-account state was not rendered.' }
                if ($switch.Current.IsEnabled -ne ($scenario -in @('saved','damaged'))) { throw 'Switch button enabled state does not match account availability.' }
                if ($scenario -ne 'empty') {
                    # The owner-drawn ListBox's MSAA provider need not expose individual item names.
                    if (@($elements | Where-Object { $_.Current.Name -eq '已保存 2 个账号' }).Count -eq 0) { throw 'Saved account catalog count was not rendered.' }
                }
                if (@($elements | Where-Object { $_.Current.Name -eq '刷新账号列表' }).Count -ne 1) {
                    foreach ($element in $elements) { Write-Output ($element.Current.ClassName + ' | ' + $element.Current.Name) }
                    throw 'Accessible search refresh button was not rendered.'
                }
                if (@($elements | Where-Object { $_.Current.Name -eq '刷新额度' }).Count -ne 0) { throw 'Manual network quota refresh is still present.' }
                if ($scenario -eq 'empty' -and @($elements | Where-Object { $_.Current.Name -eq '未登录' }).Count -eq 0) { throw 'Missing login was not identified.' }
                if ($scenario -eq 'current' -and @($elements | Where-Object { $_.Current.Name -eq '当前登录已置顶' }).Count -eq 0) { throw 'Saved current login was not identified.' }
                if ($scenario -eq 'unknown' -and @($elements | Where-Object { $_.Current.Name -eq '当前登录 · 未保存' }).Count -eq 0) { throw 'Unknown login was confused with missing login.' }
                if ($scenario -eq 'damaged' -and @($elements | Where-Object { $_.Current.Name -eq '无法识别当前登录' }).Count -eq 0) { throw 'Unreadable login was confused with missing login.' }
                if ($null -eq $healthySince) { $healthySince = [DateTime]::UtcNow }
                # Keep pumping the real Application.Run loop long enough to catch deferred handle/layout failures.
            } while (($null -eq $healthySince -or ([DateTime]::UtcNow - $healthySince).TotalSeconds -lt 2) -and [DateTime]::UtcNow -lt $deadline)
            if ($null -eq $healthySince) {
                if ($null -ne $main) {
                    foreach ($control in $main.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
                        Write-Output ($control.Current.AutomationId + ' | ' + $control.Current.ClassName + ' | ' + $control.Current.Name)
                    }
                }
                throw ('Native application did not render: ' + $scenario)
            }
            Write-Output ('PASS Native startup: ' + $scenario + ' (PID/title/UIA controls + stable message loop)')
        } finally {
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
            }
            $process.Dispose()
        }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}
