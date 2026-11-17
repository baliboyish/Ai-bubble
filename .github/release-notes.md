AI Bubble for Windows brings ChatGPT, Claude and Muse into one floating desktop bubble.

Download **AI-Bubble-Windows-x64.zip**, extract all files to a folder, and run **AIBubbleWin.exe**. No installer or administrator access is required.

- Sign in to each service with your existing account. No AI API keys.
- Click to open the last assistant; hold and drag to move the bubble.
- Hover to switch assistants; right-click for size, appearance and other settings.
- Chat opens beside the bubble and supports resizing and pinning.
- Separate saved WebView2 sessions for each assistant.

Requires Windows 10/11 x64, .NET Framework 4.8 and the Microsoft Edge WebView2 Runtime.

The Windows workflow builds this download from the tagged source and runs 36 behavior/layout assertions before release. Interactive logins were also tested by the maintainer; CI does not sign in to accounts or test uploads/voice.

### Changes in 1.0.1

- Fixed file attach in the Claude mini chat: the window now keeps a minimum width so claude.ai shows its desktop "Upload from computer" dialog instead of the mobile bottom sheet that broke the attach.
- The host file chooser is now intercepted for the Claude window too, with a per-assistant dialog title ("Attach files to Claude"). ChatGPT and Muse behavior is unchanged.

This is an unsigned, unofficial community app. See the repository README, MIT license and third-party notices.
