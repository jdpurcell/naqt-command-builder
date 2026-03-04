# naqt Command Builder

Small single-page tool for building inputs to [naqt](https://github.com/jdpurcell/naqt) and [install-qt-action](https://github.com/jdpurcell/install-qt-action) (my fork). Entirely self-contained and even works offline (no background network calls)!

## How It's Made

A lot of the core logic was written in C# and AI-ported to JavaScript. I maintain some of the data structures (arch/extension availability by version) manually, but thankfully I have a C# utility to analyze module availability and auto-generate that one. All UI and other JavaScript is entirely AI-generated with careful prompting and manual testing/code review.

## Inspiration

Thanks to [@ddalcino](https://github.com/ddalcino) for the original idea in [aqt-list-server](https://ddalcino.github.io/aqt-list-server/).