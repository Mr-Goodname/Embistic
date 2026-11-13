param([string]$Output = "$PSScriptRoot\..\artifacts\window.png",[string]$Title,[switch]$Screen)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className,string title);
}
'@
$process = Get-Process Embistic | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (!$process) { throw 'Embistic window is not running.' }
$handle=$process.MainWindowHandle
if($Title){
 Add-Type -AssemblyName UIAutomationClient
 Add-Type -AssemblyName UIAutomationTypes
 $condition=[System.Windows.Automation.AndCondition]::new(
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id),
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Title))
 $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
 if(!$window){$window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle).FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)}
 $handle=if($window){[IntPtr]$window.Current.NativeWindowHandle}else{[IntPtr]::Zero}
}
if($handle -eq [IntPtr]::Zero){throw 'Requested window not found.'}
$rect = New-Object WindowCapture+RECT
[WindowCapture]::GetWindowRect($handle, [ref]$rect) | Out-Null
$bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
if($Screen){$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)}
else{
 $hdc = $graphics.GetHdc()
 try { [WindowCapture]::PrintWindow($handle,$hdc,2) | Out-Null }
 finally { $graphics.ReleaseHdc($hdc) }
}
$fullOutput = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutput)) | Out-Null
$bitmap.Save($fullOutput,[Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
Write-Output $fullOutput
