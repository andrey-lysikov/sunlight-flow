# Changelog

## 1.5
* Full reload logics, we follow from the WDL, but control lighting and color
* Update installer

## 1.1
* Update sunlight calculation
* Update installer and updater

## 1.0

First release.

### Features

- Brightness follows the sun. Cloud cover taken into account through Open-Meteo
- One tray icon. Click toggles control, double click on the dimmed icon quits
- The icon is drawn at runtime: a sun whose rays grow with its height, a moon
  with the current phase after sunset.
- Location worked out on its own: latitude by IP, longitude by time zone.
- Optional load reaction: blend towards a busy colour, or breathe with the main
  one. No elevation and no drivers
