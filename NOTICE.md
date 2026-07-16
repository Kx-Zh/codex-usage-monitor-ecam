# Notices and attribution

Codex ECAM Monitor is an independent community project. It is not affiliated
with, endorsed by, or sponsored by OpenAI, Airbus, Boeing, FlyByWire
Simulations, or FlightGear.

## ECAM display font

`assets/ECAMFontRegular.ttf` is the **FBW Display-EIS** font from the FlyByWire
Simulations aircraft project:

https://github.com/flybywiresim/aircraft/tree/master/fbw-a32nx/src/base/flybywire-aircraft-a320-neo/html_ui/Fonts/fbw-a32nx

The FlyByWire aircraft project is distributed under the GNU General Public
License version 3. This repository is therefore distributed under GPL-3.0;
see `LICENSE`.

## Display design references

The main dial, color hierarchy, typography, ticks, and needle treatment are
inspired by the visual language of the Airbus A320 ECAM. This project is not
an exact reproduction of an aircraft instrument.

The lower-right `CTX K` quantity and 240-degree capacity arc are inspired by
the fuel-quantity presentation in the FlightGear Boeing 737 EICAS reference:

- https://svn.code.sf.net/p/flightgear/fgaddon/trunk/Aircraft/737-800/Models/Instruments/EICAS/upperEICAS.svg
- https://svn.code.sf.net/p/flightgear/fgaddon/trunk/Aircraft/737-800/Models/Instruments/EICAS/upperEICAS.nas

No FlightGear runtime code is included in this repository.

## Codex CLI

Codex CLI is an external runtime dependency and is not included in this
repository or its release packages. The official OpenAI Codex repository is:

https://github.com/openai/codex

The upstream Codex CLI repository is licensed under Apache-2.0. This project's
GPL-3.0 license applies to Codex ECAM Monitor, not to a separately installed
Codex CLI.

## Trademarks

Codex and OpenAI, Airbus, Boeing, FlyByWire, FlightGear, and all related names,
logos, and marks belong to their respective owners. Their use here is solely
descriptive and does not imply affiliation or endorsement.
