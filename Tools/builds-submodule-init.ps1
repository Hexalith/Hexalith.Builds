git submodule init https://github.com/Hexalith/Hexalith.Builds.git
git submodule update Hexalith.Builds
git submodule foreach git pull origin main
ln -s ./Hexalith.Builds/.editorconfig .editorconfig
