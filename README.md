This is a workaround in C# for the problem described [here](https://github.com/MicrosoftEdge/WebView2Feedback/issues/2236)

In a nutshell the problem is, that WebView2 shows up as "Microsoft Edge WebView2" in the volume mixer of Windows (sndvol.exe) and the Edge program icon is displayed, instead of the application's icon.

This workaround leverages the Windows Core Audio API in order to change the text and the icon. 
For this NAudio is used. The used NAudio version is 1.9.0, which is not the latest one, but I am familiar with this version and the code is more meant to be a proof of concept than production-ready code.

There is the limitation, that the change of the icon is not reflected in the Windows volume mixer, if the mixer is already opened. The change only happens once the mixer is closed and opened again. The displayed application name, however, is changed immediately. This is at least vaild for my Windows 10 machine. 
