# chat-tabcomplete

Tab completion for the default FiveM `chat` resource. Type `/gi` and press **Tab** to get
`/givecar `; press Tab again to cycle through the other matches.

It reads the suggestion list the chat already renders, so every registered command works
with no per-command setup.

## Install

1. Copy `tab-complete.js` to `resources/[gameplay]/chat/dist/`.
2. In `dist/ui.html`, add after the `chat.js` script tag:
   `<script defer="defer" src="tab-complete.js"></script>`
3. In the chat `fxmanifest.lua`, add `'dist/tab-complete.js',` to the `files` list.
4. Restart the server.

Note: this patches a copy of the stock chat resource, so re-apply after updating `chat`.
