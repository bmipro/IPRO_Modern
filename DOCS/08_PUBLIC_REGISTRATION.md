# Public Registration and Account Provisioning

## Register a New Agent

1. Open the public registration page.
2. Enter required contact information.
3. Enter company, location, and business phone information.
4. Select a business type. The choice controls relevant starter content.
5. Select an active package.
6. Read the terms and conditions.
7. Enter the changing verification code.
8. Accept the terms.
9. Submit registration.

Optional information such as designation, company address, fax, and mobile phone may be left blank.

## Automatic Account Setup

After successful registration, IPRO:

1. Normalizes the email address to lowercase and checks uniqueness.
2. Creates a username from first name and last name.
3. Adds a numeric suffix when the username already exists.
4. Generates a temporary password.
5. Requires a password change on first login.
6. Assigns a temporary `FirstNameLastName.247Advisers.com` domain.
7. Assigns the selected package and business type.
8. Creates starter website content using the matching business type/package rules.
9. Displays the registration success page.
10. Sends the welcome email when SendGrid is configured.

## Duplicate Email

An email already associated with an agent cannot be registered again. The registrant should sign in or contact IPRO support.

## Registration Success Page

The page displays:

- Agent name
- Temporary website address
- Username
- Temporary password
- First-login password instructions
- Sign-in and website links

The registrant should keep these details until the password has been changed.

## Welcome Email

The welcome email includes the same account information and temporary website link. If delivery fails, the account is still created and the success page displays the credentials.

Super Admin can correct the email and reset the password later.

