"You are an expert in software development and version control systems, specifically Git. Your task is to generate a detailed commit message following the **Conventional Commits** specification. The Conventional Commits format helps standardize commit messages to make them machine-readable and human-understandable, often used for automated changelog generation and semantic versioning.

Here’s how to structure the commit message based on the Conventional Commits rules:

1. **Format**: The commit message must follow this structure:
   ```
   <type>(<scope>): <short description>
   <BLANK LINE>
   <optional body>
   <BLANK LINE>
   <optional footer>
   ```

2. **Components**:
   - **Type**: A single word indicating the nature of the change. Common types include:
     - `feat`: A new feature.
     - `fix`: A bug fix.
     - `docs`: Documentation changes.
     - `style`: Code style changes (formatting, white-space, etc.).
     - `refactor`: Code refactoring without adding features or fixing bugs.
     - `test`: Adding or modifying tests.
     - `chore`: Miscellaneous changes (e.g., updating build tools, config files).
     - `perf`: Performance improvements.
     - `ci`: Changes to CI/CD pipelines.
     - `build`: Changes affecting the build system or dependencies.
   - **Scope** (optional): A word or phrase in parentheses after the type, specifying the part of the codebase affected (e.g., `api`, `ui`, `database`).
   - **Short Description**: A concise summary (50 characters or less) of the change, written in the imperative mood (e.g., 'add', 'fix', 'update').
   - **Body** (optional): Additional context or details about the change, written in full sentences, wrapped at 72 characters per line. Explain *what* was changed and *why*, not *how*.
   - **Footer** (optional): Metadata such as issue tracker references (e.g., 'Fixes #123') or breaking change notifications (e.g., 'BREAKING CHANGE: removes old API endpoint').

3. **Rules**:
   - Start the short description with a lowercase letter (unless it’s a proper noun).
   - Do not end the short description with a period.
   - Use a blank line between the description, body, and footer.
   - If the change introduces a breaking change, include 'BREAKING CHANGE:' in the footer with an explanation.

4. **Example Scenarios**:
   - For a new feature:
     ```
     feat(auth): add user login endpoint
     
     Implement a new POST /login endpoint to authenticate users with
     email and password. Includes validation and JWT generation.
     
     Closes #45
     ```
   - For a bug fix with a breaking change:
     ```
     fix(database): update schema for user table
     
     Modify the user table to include a required 'created_at' column,
     fixing an issue with missing timestamps in queries.
     
     BREAKING CHANGE: existing tables must be migrated to include the
     new required column.
     ```
   - For a documentation update:
     ```
     docs(readme): update installation instructions
     
     Add a section on installing dependencies with npm and clarify
     system requirements for running the project.
     ```

**Task**: Based on the rules and examples above, create a commit message for the following scenario: You’ve just refactored the payment processing module to improve readability and remove redundant code. The changes affect the 'payments' scope, and there’s no new functionality or bug fixes involved. Optionally, include a reference to a ticket number 'PAY-789' in the footer."

---

**Expected Output from the Prompt:**

```
refactor(payments): simplify payment processing logic

Restructure the payment processing module to improve code
readability and eliminate duplicate validation checks. No
functional changes were introduced.

Ref PAY-789
```

---

This prompt provides a clear framework for generating a Conventional Commits message, adaptable to any specific scenario by tweaking the details (type, scope, description, etc.). Let me know if you'd like me to refine it further or generate a commit message for a different scenario!