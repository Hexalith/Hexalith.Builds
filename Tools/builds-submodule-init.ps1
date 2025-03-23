git submodule init https://github.com/Hexalith/Hexalith.Builds.git
git submodule update Hexalith.Builds
git submodule foreach git pull origin main
New-Item -ItemType SymbolicLink -Path ".editorconfig" -Target "./Hexalith.Builds/.editorconfig" -Force
