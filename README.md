# naqt Command Builder

Small single-page tool for building inputs to [naqt](https://github.com/jdpurcell/naqt) and [install-qt-action](https://github.com/jdpurcell/install-qt-action) (my fork).

## Notes

* Available hosts, targets, versions, arches, and extensions are all logic-driven. I will have to update this page to keep up with each Qt release.
* Module entries are fetched on-demand from Qt's update server only when `Install modules` is checked. Otherwise, the page makes no background network calls.

## How It's Made

I wrote the core data structures and logic in C# which was then AI-ported to JavaScript. All UI and other JavaScript is entirely AI-generated with careful prompting and manual testing/code review.

## Inspiration

Thanks to [@ddalcino](https://github.com/ddalcino) for the original idea in [aqt-list-server](https://ddalcino.github.io/aqt-list-server/).