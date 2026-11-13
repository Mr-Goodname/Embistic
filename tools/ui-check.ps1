param([ValidateSet('dump','click','select','search','volume')][string]$Action='dump',[string]$Id,[string]$Name,[string]$Value)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$process=Get-Process Embistic | Where-Object {$_.MainWindowHandle -ne 0} | Select-Object -First 1
if (!$process) { throw 'App not running' }
$root=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if ($Action -eq 'dump') {
 $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($item in $all) {
  if (!$item.Current.IsOffscreen -and ($item.Current.Name -or $item.Current.AutomationId)) {
   [PSCustomObject]@{Id=$item.Current.AutomationId;Name=$item.Current.Name;Type=$item.Current.ControlType.ProgrammaticName;Rect=$item.Current.BoundingRectangle.ToString()}
  }
 }
 exit
}
if ($Id) {$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)}
else {$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name)}
$element=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
if (!$element) {throw "Control not found: $Id $Name"}
if ($Action -in 'click','select') {
 $patternId=if($Action -eq 'click'){[System.Windows.Automation.InvokePattern]::Pattern}else{[System.Windows.Automation.SelectionItemPattern]::Pattern}
 $pattern=$null
 while($element -and !$element.TryGetCurrentPattern($patternId,[ref]$pattern)){$element=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element)}
 if(!$element){throw "Control does not support $Action"}
}
switch($Action) {
 'click' { ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
 'select' { ([System.Windows.Automation.SelectionItemPattern]$element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
 'search' { ([System.Windows.Automation.ValuePattern]$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value) }
 'volume' { ([System.Windows.Automation.RangeValuePattern]$element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)).SetValue([double]$Value) }
}
Write-Output "UI action completed: $Action"
