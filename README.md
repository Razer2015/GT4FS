# GT4FS

Tool to extract and repack files from Gran Turismo 4, Tourist Trophy & other builds based on these games (TT Demo, GTHD, etc).

## Usage

To extract:
* `GT4FS extract -r <GT4.VOL/TT.VOL>`

To repack: 
* `GT4FS pack -r <extracted game folder> -g <game>`, where `game` can be either: 
  * `GT4` - Gran Turismo 4 (any region)
  * `GT4_ONLINE` - Gran Turismo 4 Online Test Version (US/JP)
  * `TT` - Tourist Trophy (any region)
  * `GTHD` - Gran Turismo HD (PS3)
  * `TT_DEMO` - Tourist Trophy Demo
  * `GT4_MX5_DEMO` - Gran Turismo 4 Mazda Demo
  * `GT4_FIRST_PREV` - Gran Turismo 4 First Preview

# Notes
IML2ISO is recommended, to build an iso off the game.

https://github.com/pez2k/gt2tools/tree/master/ISOLayerMerge, to merge two iso layers (i.e GT4).

## Compiling
Visual Studio 2019 & .NET Core Development Tools (.NET 5) required.
