# Shared paths. Included with `source scripts/env.sh`.
ONI_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
STEAM_DIR="${STEAM_DIR:-$HOME/.local/share/Steam}"
ONI_GAME_DIR="${ONI_GAME_DIR:-$STEAM_DIR/steamapps/common/OxygenNotIncluded}"
ONI_MANAGED_DIR="$ONI_GAME_DIR/OxygenNotIncluded_Data/Managed"
# The game runs under Proton: user data lives inside the prefix.
ONI_PREFIX_USER="$STEAM_DIR/steamapps/compatdata/457140/pfx/drive_c/users/steamuser"
ONI_USER_DIR="${ONI_USER_DIR:-$ONI_PREFIX_USER/Documents/Klei/OxygenNotIncluded}"
ONI_DEV_MODS_DIR="$ONI_USER_DIR/mods/Dev"
ONI_PLAYER_LOG="$ONI_PREFIX_USER/AppData/LocalLow/Klei/Oxygen Not Included/Player.log"
ONI_APP_MANIFEST="$STEAM_DIR/steamapps/appmanifest_457140.acf"
export ONI_ROOT ONI_GAME_DIR ONI_MANAGED_DIR ONI_USER_DIR ONI_DEV_MODS_DIR ONI_PLAYER_LOG
