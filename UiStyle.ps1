# Shared E palette and native control treatments. No owner-drawn buttons: native
# keyboard activation, default/cancel semantics and accessibility stay intact.
function Get-SwitchPalette {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if ([Windows.Forms.SystemInformation]::HighContrast) {
        return @{
            Ink = [Drawing.SystemColors]::WindowText; Muted = [Drawing.SystemColors]::WindowText
            Surface = [Drawing.SystemColors]::Window; Sidebar = [Drawing.SystemColors]::Window
            Border = [Drawing.SystemColors]::WindowText; Primary = [Drawing.SystemColors]::Highlight
            PrimaryBorder = [Drawing.SystemColors]::WindowText; Danger = [Drawing.SystemColors]::Highlight
            ErrorSurface = [Drawing.SystemColors]::Window; Selection = [Drawing.SystemColors]::Highlight
            SelectionInk = [Drawing.SystemColors]::HighlightText; Error = [Drawing.SystemColors]::WindowText
        }
    }
    return @{
        Ink = [Drawing.ColorTranslator]::FromHtml('#202a34'); Muted = [Drawing.ColorTranslator]::FromHtml('#586979')
        Surface = [Drawing.Color]::White; Sidebar = [Drawing.ColorTranslator]::FromHtml('#f0f2f5')
        Border = [Drawing.ColorTranslator]::FromHtml('#80909e'); Primary = [Drawing.ColorTranslator]::FromHtml('#194d76')
        PrimaryBorder = [Drawing.ColorTranslator]::FromHtml('#103957'); Danger = [Drawing.ColorTranslator]::FromHtml('#914536')
        ErrorSurface = [Drawing.ColorTranslator]::FromHtml('#fff4f0'); Selection = [Drawing.ColorTranslator]::FromHtml('#dce8f1')
        SelectionInk = [Drawing.ColorTranslator]::FromHtml('#123e60'); Error = [Drawing.ColorTranslator]::FromHtml('#914536')
    }
}

function Set-SwitchButtonStyle {
    param([Parameter(Mandatory = $true)]$Button, [ValidateSet('Primary','Secondary','Danger')][string]$Role = 'Secondary')
    $palette = Get-SwitchPalette
    $normal = [Drawing.Color]::White
    $foreground = [Drawing.ColorTranslator]::FromHtml('#202a34')
    $edge = [Drawing.ColorTranslator]::FromHtml('#80909e')
    $hover = [Drawing.ColorTranslator]::FromHtml('#edf2f6')
    $pressed = [Drawing.ColorTranslator]::FromHtml('#dce6ee')
    if ($Role -eq 'Primary') {
        $normal = [Drawing.ColorTranslator]::FromHtml('#194d76'); $foreground = [Drawing.Color]::White; $edge = [Drawing.ColorTranslator]::FromHtml('#103957')
        $hover = [Drawing.ColorTranslator]::FromHtml('#245e8b'); $pressed = [Drawing.ColorTranslator]::FromHtml('#103957')
    } elseif ($Role -eq 'Danger') {
        $normal = [Drawing.ColorTranslator]::FromHtml('#914536'); $foreground = [Drawing.Color]::White; $edge = [Drawing.ColorTranslator]::FromHtml('#713226')
        $hover = [Drawing.ColorTranslator]::FromHtml('#a35040'); $pressed = [Drawing.ColorTranslator]::FromHtml('#713226')
    }
    $Button.FlatStyle = 'Flat'
    $Button.UseVisualStyleBackColor = $false
    $Button.FlatAppearance.MouseOverBackColor = $hover
    $Button.FlatAppearance.MouseDownBackColor = $pressed
    $Button | Add-Member -NotePropertyName SwitchButtonRole -NotePropertyValue $Role -Force
    if (-not $Button.AccessibleName) { $Button.AccessibleName = $Button.Text.Replace('&', '') }
    $update = {
        if ([Windows.Forms.SystemInformation]::HighContrast) {
            # Native high-contrast rendering pairs foreground and hover colors.
            $Button.FlatStyle = 'System'
            $Button.UseVisualStyleBackColor = $true
            $Button.BackColor = [Drawing.SystemColors]::Control
            $Button.ForeColor = if ($Button.Enabled) { [Drawing.SystemColors]::ControlText } else { [Drawing.SystemColors]::GrayText }
            $Button.FlatAppearance.BorderColor = [Drawing.SystemColors]::ControlText
        } else {
            $Button.FlatStyle = 'Flat'
            $Button.UseVisualStyleBackColor = $false
            $Button.FlatAppearance.MouseOverBackColor = $hover
            $Button.FlatAppearance.MouseDownBackColor = $pressed
            $Button.BackColor = if ($Button.Enabled) { $normal } else { [Drawing.ColorTranslator]::FromHtml('#e3e8ed') }
            $Button.ForeColor = if ($Button.Enabled) { $foreground } else { [Drawing.ColorTranslator]::FromHtml('#657482') }
            $Button.FlatAppearance.BorderColor = if ($Button.Enabled) { $edge } else { [Drawing.ColorTranslator]::FromHtml('#bec8d1') }
        }
        $Button.FlatAppearance.BorderSize = if ($Button.Focused -and $Button.Enabled) { 2 } else { 1 }
        $Button.Invalidate()
    }.GetNewClosure()
    $Button.Add_GotFocus($update)
    $Button.Add_LostFocus($update)
    $Button.Add_EnabledChanged($update)
    $Button.Add_SystemColorsChanged($update)
    & $update
}

function Set-SwitchInputStyle {
    param([Parameter(Mandatory = $true)]$TextBox)
    # Keep the TextBox in its original parent so control names, tab order and
    # screen-reader relationships stay stable. Only the surrounding frame is new.
    $palette = Get-SwitchPalette
    $initialInsetX = [Math]::Max(7, [int][Math]::Round($TextBox.Font.Height * 0.35))
    $initialInsetY = [Math]::Max(5, [int][Math]::Round($TextBox.Font.Height * 0.27))
    $TextBox.BorderStyle = 'None'
    # The existing bounds describe the OUTER field, not the glyph origin.
    $TextBox.Left += $initialInsetX
    $TextBox.Top += $initialInsetY
    $TextBox.Width -= 2 * $initialInsetX
    $frame = New-Object Windows.Forms.Panel
    $frame.Name = $TextBox.Name + 'Frame'
    $frame.TabStop = $false
    $frame.AccessibleRole = 'None'
    $frame.Padding = New-Object Windows.Forms.Padding(1)
    $inside = New-Object Windows.Forms.Panel
    $inside.Dock = 'Fill'
    $inside.BackColor = $palette.Surface
    $inside.TabStop = $false
    $inside.AccessibleRole = 'None'
    $frame.Controls.Add($inside)
    $TextBox.Parent.Controls.Add($frame)
    $frame.SendToBack()
    $TextBox.BackColor = $palette.Surface
    $TextBox.ForeColor = $palette.Ink
    $TextBox | Add-Member -NotePropertyName SwitchInputFrame -NotePropertyValue $frame -Force
    $TextBox | Add-Member -NotePropertyName SwitchInputHasError -NotePropertyValue $false -Force
    $layout = {
        $verticalInset = [Math]::Max(5, [int][Math]::Round($TextBox.Font.Height * 0.27))
        $horizontalInset = [Math]::Max(7, [int][Math]::Round($TextBox.Font.Height * 0.35))
        $frame.Bounds = New-Object Drawing.Rectangle(($TextBox.Left - $horizontalInset), ($TextBox.Top - $verticalInset), ($TextBox.Width + 2 * $horizontalInset), ($TextBox.Height + 2 * $verticalInset))
    }.GetNewClosure()
    $update = {
        $colors = Get-SwitchPalette
        $frame.BackColor = if ($TextBox.SwitchInputHasError) { $colors.Error } elseif ($TextBox.Focused) { $colors.Primary } else { $colors.Border }
        $frame.Padding = New-Object Windows.Forms.Padding($(if ($TextBox.Focused -or $TextBox.SwitchInputHasError) { 2 } else { 1 }))
        $inside.BackColor = if ($TextBox.Enabled) { $colors.Surface } else { [Drawing.SystemColors]::Control }
        $TextBox.BackColor = $inside.BackColor
        $TextBox.ForeColor = if ($TextBox.Enabled) { $colors.Ink } else { [Drawing.SystemColors]::GrayText }
    }.GetNewClosure()
    $TextBox | Add-Member -NotePropertyName RefreshSwitchInputStyle -NotePropertyValue $update -Force
    $TextBox.Add_LocationChanged($layout)
    $TextBox.Add_SizeChanged($layout)
    $TextBox.Add_FontChanged($layout)
    # Parent scaling visits sibling controls separately. Recompute after that
    # layout pass so the decorative frame cannot be scaled a second time.
    $TextBox.Parent.Add_Layout($layout)
    $TextBox.Add_GotFocus($update)
    $TextBox.Add_LostFocus($update)
    $TextBox.Add_EnabledChanged($update)
    $TextBox.Add_SystemColorsChanged($update)
    & $layout
    & $update
    # Clicking the padded area should focus the same native input.
    $focusInput = { [void]$TextBox.Focus() }.GetNewClosure()
    $inside.Add_Click($focusInput)
    $frame.Add_Click($focusInput)
}

function Set-SwitchInputError {
    param([Parameter(Mandatory = $true)]$TextBox, [bool]$HasError)
    $TextBox.SwitchInputHasError = $HasError
    & $TextBox.RefreshSwitchInputStyle
}
