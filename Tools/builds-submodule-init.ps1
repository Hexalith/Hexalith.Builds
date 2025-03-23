# Initialize the Hexalith.Builds Git submodule from the specified repository
git submodule init https://github.com/Hexalith/Hexalith.Builds.git

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update Hexalith.Builds

# Checkout the main branch in the Hexalith.Builds submodule
git submodule foreach git checkout main

# Create a symbolic link for the .editorconfig file pointing to the one in the Hexalith.Builds submodule
# -Force overwrites the link if it already exists
New-Item -ItemType SymbolicLink -Path ".editorconfig" -Target "./Hexalith.Builds/.editorconfig" -Force
