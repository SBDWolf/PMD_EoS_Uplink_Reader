# PMD:EoS Uplink Reader (C#)

This is an application to read USB data getting sent from Pokémon Mystery Dungeon: Explorers of Sky's Speedrun Mod, running on a DSPico. It stores its data into memory-mapped files, which can then be read by a Livesplit Autosplitter or other application. Most of this application is vibe-coded.

Note: initiating the connection during the intro before the Top Level Menu seems to often lead to in-game crashes. It seems to be more stable to wait until you're at the Top Level Menu to connect.
