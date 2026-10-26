Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class Theme {
  [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
  public static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subId);
}
'@

$form = New-Object System.Windows.Forms.Form
$form.Text = 'scroll-test'
$form.ClientSize = New-Object System.Drawing.Size 320, 220
$form.BackColor = [System.Drawing.Color]::FromArgb(0x0F, 0x17, 0x2A)
$form.TopMost = $true
$form.StartPosition = 'CenterScreen'

$list = New-Object System.Windows.Forms.ListBox
$list.Location = New-Object System.Drawing.Point 10, 10
$list.Size = New-Object System.Drawing.Size 300, 200
$list.BorderStyle = 'None'
$list.BackColor = [System.Drawing.Color]::FromArgb(0x16, 0x20, 0x36)
$list.ForeColor = [System.Drawing.Color]::White
$list.IntegralHeight = $false

for ($i = 1; $i -le 30; $i++) { $list.Items.Add("строка $i") | Out-Null }

$form.Controls.Add($list)

$form.Add_Shown({
    [Theme]::SetWindowTheme($list.Handle, 'DarkMode_Explorer', $null) | Out-Null
    Start-Sleep -Milliseconds 900

    $bmp = New-Object System.Drawing.Bitmap 320, 220
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($form.Left, $form.Top, 0, 0, $bmp.Size)
    $bmp.Save("$PWD\.tmp-scrolltest.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()

    $form.Close()
})

[void]$form.ShowDialog()
