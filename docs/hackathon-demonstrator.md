# Hackathon demonstrator

ASHA's competition slice demonstrates one human-facing loop: talk naturally,
let ASHA look at a deliberately permitted part of the desktop, and allow a
small visible action only while an explicit control session is active.

## Start

1. Install .NET 8 and use Windows 11.
2. Bring a Groq API key for the research demonstrator. Run
   `configure-groq.bat`; no key belongs in this repository.
3. Ensure the local speech service is available on port 9010.
4. Run `start-asha.bat`.
5. Open ASHA's controls, start a shared-attention session, choose a desktop
   awareness mode, allow one-view sharing, and enable computer control.

Computer control is unavailable without both the retained session and the
visible control lease. The blue-violet desktop frame is the default presence
indicator and can be disabled only in settings.

## Suggested ninety-second demonstration

Use non-sensitive windows and files.

1. Say, “Open LM Studio.” ASHA resolves the installed Start application,
   opens or activates it, and verifies a visible window.
2. Say, “Look at the foreground window. What do you see?” ASHA requests that
   window rather than following the mouse; the temporary blue frame shows the
   exact shared region.
3. Say, “Look at the left side of this monitor and find the application icon.”
   ASHA requests the left-screen view.
4. Ask ASHA to move its pointer to a clearly visible, harmless target. Then
   ask for a single click. ASHA shows the intended point, checks the exposed
   top-layer surface, moves the physical pointer, and records the action.
5. Say, “Open my Downloads folder.” ASHA resolves the existing folder and
   opens Explorer without changing any file.
6. Stop computer control. Ask ASHA to click again; the tool must refuse.
7. Open Sessions to show that conversation, visual evidence, permissions, and
   control events share one local timeline.

## Control contract

The application tool receives a display name only. ASHA compares it with the
Windows Start-app catalog and launches the AppID supplied by Windows. It does
not execute model-generated paths or commands.

The folder tool accepts an existing display name or local path. It performs a
read-only Explorer open and rejects protected Windows, program, and
application-data locations.

Pointer input uses Windows `SendInput`. A coordinate is accepted only inside
the current model-visible desktop rectangle. Immediately before input, ASHA
resolves and records the exposed top-layer surface at that coordinate. The
click therefore applies to the human-visible surface, never an assumed hidden
window. A follow-up evidence capture is retained for verification.

ASHA can request five view scopes: pointer area, foreground window, entire
desktop, left half of the current monitor, and right half. Large scopes are
downscaled before provider transfer while preserving their desktop coordinate
mapping. The temporary capture frame makes the transferred region visible to
the person.

## Deliberate limits

- No arbitrary shell, terminal, URL, executable path, registry, or system
  folder access is exposed to the model.
- No password, token, recovery code, or payment-detail typing.
- No hidden or persistent control after the person stops the visible lease.
- A sent click is not reported as a successful outcome until the resulting
  visible state is verified.
- Groq is for research and demonstration. Private deployments replace the
  provider with a suitable local model and their own policy controls.
