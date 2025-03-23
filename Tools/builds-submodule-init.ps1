# Initialize the Hexalith.Builds Git submodule from the specified repository
git submodule init https://github.com/Hexalith/Hexalith.Builds.git

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update Hexalith.Builds

# Execute 'git pull origin main' for each submodule to ensure they're all up to date
git submodule foreach git pull origin main

# Create a symbolic link for the .editorconfig file pointing to the one in the Hexalith.Builds submodule
# -Force overwrites the link if it already exists
New-Item -ItemType SymbolicLink -Path ".editorconfig" -Target "./Hexalith.Builds/.editorconfig" -Force
