# Contributing

Fork this repository, create a branch in your fork, make your change, then open a pull request.

Build with `./build.ps1` and run `./tests/run.ps1` on Windows before submitting. Keep website login and browser profiles local; never commit cookies, credentials, personal settings, diagnostic logs, or account data.

For service integration changes, state which website and flow you tested. Automated checks do not prove that authentication, uploads or voice work. Keep Muse styling scoped to Muse origins.

The namespace and some filenames retain MuseBubbleWin names because this project builds on that MIT-licensed app. The output executable is AIBubbleWin.exe.

For a new release, increment Version in AIBubbleWin.csproj. A successful main-branch workflow publishes the version only if that release does not exist. Existing release assets are never silently replaced.
