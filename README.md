# ETS2.Brake
A OMSI-like brake system for ETS and ATS.

# Installation
* [First you'll need to install virtual joystick](http://vjoystick.sourceforge.net/site/index.php/download-a-install/download).
* Then you'll have to replace your bindings [with this one](https://github.com/redbaty/ETS2.Brake/blob/master/controls.sii) (_It goes to /Documents/Euro Truck Simulator 2/Profiles/YOURPROFILE_)
* Then all you'll need to do is either compile the code or [get the binaries](https://github.com/redbaty/ETS2.Brake/releases/latest)
* Execute the program and have fun!

# Settings file
In the settings file (_config.json_) you'll find these properties:

* **IncreaseDelay** : The time between increasing inside the increasing loop.
* **StartIncreaseRatio** : The base increasing value.
* **IsIncreaseRatioEnabled** : Set if increasing the increase ratio is enabled. (A.K.A Exponential increase)
* **ResetIncreaseRatioTimeSpan** : The time it takes to reset the increase value back to it's base after releasing the 'S' key.
* **ShowMemoryUsage** : Enables/disables the current memory usage display.
