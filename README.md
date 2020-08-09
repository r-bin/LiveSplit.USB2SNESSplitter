# LiveSplit.USB2SNESSplitter

LiveSplit auto splitter for SD2SNES. A layout component using the [Usb2Snes websocket protocol](https://www.usb2snes.com).

For emulators [Scritable Auto Splitter](https://github.com/LiveSplit/LiveSplit#auto-splitters) should be preferred.

## Installation

Download the latest [release](https://github.com/r-bin/LiveSplit.USB2SNESSplitter/releases) and add the DLL files to the `Components` directory in LiveSplit:

```
LiveSplit (Your LiveSplit directory)
├─Components
│ ├─LiveSplit.USB2SNESSplitter.dll
│ └─websocket-sharp.dll
└─LiveSplit.exe (Your LiveSplit starter)
```

## Usage

* Add `USB2SNES AutoSplitter` to your LiveSplit layout
  * Not to be confused with "Scritable Auto Splitter", which only works with emulators
  * `Edit Layout` > `(+)` > `Controller` > `USB2SNES AutoSplitter`
  * If the entry `USB2SNES AutoSplitter` does not exist go back to the [Installation](#installation) section
  * A green/yellow/red stripe should appear at the bottom of LiveSplit, if it isn't covered
  * If an error message appears `websocket-sharp.dll` might be missing (See [Error Handling](#error-handling))
* Open the layout editor and edit the newly added `USB2SNES AutoSplitter`
* Connect to [Usb2Snes](http://usb2snes.com/) by hitting `Autodetect`
  * If the connection bar does not turn green, SD2SNES was not connected properly with USB2SNES (See [Error Handling](#error-handling))
  * [Qusb2Snes](https://skarsnik.github.io/QUsb2snes/) also works (And is compatible with emulators)
* Choose a JSON config file
  * Examples can be found in [snes-scripts](https://github.com/r-bin/snes-scripts/) (See [JSON files](#json-files))
  * ASL files are not compatible (Only with [Scritable Auto Splitter](https://github.com/LiveSplit/LiveSplit#auto-splitters))
  * The JSON file also contains the list of active splits at the top
  * If the splitter does not work, check the content of the JSON file (See [Error Handling](#error-handling))

## JSON files

Example files be found [here](https://github.com/r-bin/snes-scripts/tree/master/LiveSplit%20Auto%20Splitters/USB2SNES).

## Error Handling

Errors aren't displayed in LiveSplit. In order to find detailed error messages [DebugView](https://docs.microsoft.com/en-us/sysinternals/downloads/debugview) can be used.

## Developing JSON Files

LiveSplits current state is used to determine which splits to use and at what time:
* Based on the current game category, a set of splits is selected from the JSON file
* Based on the current split a definition is chosen
* The `splits` list therefore has to match the split list in LiveSplit and acts as config file

```json
{
   "categories":[
      {
         "name":"Any% No Verm. Skip",
         "splits":[
            "flowers",
            "thraxx",
            "magmar"
         ]
      }
   ]
}
```

`definitions` contains the list of available splits:
* The `name` is required to reference the split in `splits`
* Reading address `0x7E0ADB` must be equal (`==`) to the decimal number `74`
* Roughly 20 checks per second are being executed (Every 3rd or 4th frame)

```json
{
   "name":"saturn",
   "address":"0x0ADB",
   "value":"74",
   "operator":"=="
}
```

For more complex splits additional keywords can be used:
* `more` waits for split `a` and `b` to be `true` in consecutive checks  (i.e.: `a, b, a, b, A, b, A, b, A, b, A, B` ⟶ split)
* `next` waits for split `a` and `b` to be `true` in a time independent sequence (i.e.: `a, a, a, a, a, A, b, b, b, b, B` ⟶ split)

```json
{
   "name":"start",
   "address":"0x0ADB",
   "value":"97",
   "operator":"==",
   "next":[
      {
         "address":"0x0ADB",
         "value":"56",
         "operator":"=="
      }
   ]
}
```

## Compiling the Code

* Check out [LiveSplit](https://github.com/LiveSplit/LiveSplit)
  * Also have a look at [Common Compiling Issues](https://github.com/LiveSplit/LiveSplit#common-compiling-issues)
* Check out LiveSplit.USB2SNESSplitter and place it into the `Components` directory
* Add LiveSplit.USB2SNESSplitter to [LiveSplit](https://github.com/LiveSplit/LiveSplit) and run it