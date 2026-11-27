AI Bubble for Windows brings ChatGPT, Claude and Muse into one floating desktop bubble.

Download **AI-Bubble-Windows-x64.zip**, extract all files to a folder, and run **AIBubbleWin.exe**. No installer or administrator access is required.

- Sign in to each service with your existing account. No AI API keys.
- Click to open the last assistant, click again to hide it; hold and drag to move the bubble and any open chat together.
- Hover to switch assistants; right-click for size, appearance and other settings.
- Chat opens beside the bubble and supports resizing and pinning.
- Separate saved WebView2 sessions for each assistant.

Requires Windows 10/11 x64, .NET Framework 4.8 and the Microsoft Edge WebView2 Runtime.

The Windows workflow builds this download from the tagged source and runs 40 behavior/layout assertions before release. Interactive logins were also tested by the maintainer; CI does not sign in to accounts or test uploads/voice.

### Changes in 1.2.0

- Refreshed the floating bubble and chat frame. The header now shows colored provider icons and full names for ChatGPT, Claude and Muse.
- All three chats share one window size. Resizing one sets the size used when switching providers.
- Claude can use the same minimum window size as ChatGPT and Muse.
- File attachments open the modern Windows picker, with a classic picker fallback if it throws.

This is an unsigned, unofficial community app. See the repository README, MIT license and third-party notices.
