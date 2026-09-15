<img src="src/sun.ico" width="128" alt="icon">

# Sunlight-Flow

Motherboard lighting driven by the elevation of the sun. Off in daylight, ramping
up in a set colour towards sunset. Overcast evenings get dark earlier, and the
lighting follows.

Works through the stock Windows Dynamic Lighting interface.

[![Downloads](https://img.shields.io/github/downloads/andrey-lysikov/sunlight-flow/total?style=flat-square&label=downloads)](https://github.com/andrey-lysikov/sunlight-flow/releases/latest)
[![Release](https://img.shields.io/github/v/release/andrey-lysikov/sunlight-flow?style=flat-square&label=release)](https://github.com/andrey-lysikov/sunlight-flow/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-blue?style=flat-square)](https://github.com/andrey-lysikov/sunlight-flow/releases/latest)

## Features
- Brightness follows the sun. Cloud cover taken into account through Open-Meteo
- One tray icon. Click toggles control, double click on the dimmed icon quits
- The icon is drawn at runtime: a sun whose rays grow with its height, a moon
  with the current phase after sunset.
- Location worked out on its own: latitude by IP, longitude by time zone.
- Colours, effect and brightness follow Settings > Dynamic Lighting.
- Optional load reaction: blend towards a load colour picked from the Windows effect.

## Tech
Written in C#, for Windows 11 or newer with dynamic lighting enabled
