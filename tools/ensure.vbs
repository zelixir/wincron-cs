' WinCron 看门狗入口：由计划任务每 5 分钟调用，幂等拉起 wincron
' ctl ensure 自身会检测管道是否就绪，已运行则直接返回
Set sh = CreateObject("WScript.Shell")
sh.Run """D:\Code\wincron-cs\dist\wincron\wincronctl.exe"" ensure", 0, False
