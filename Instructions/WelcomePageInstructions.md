This is a design document for new welcome page for users who have no accounts defined in the database.
 
Right now we welcome our users in WelcomePage if they have no accounts defined. Navigation is like ShellWindow -> MailAppShell -> WelcomePage. This allows users to access ManageAccountsPage from the side bar since mail app shell has this navigation option. 
We don't want this anymore. Here are the instructions to define new welcome page (or initial startup wizard page):

- ShellWindow should not be used to navigate this new welcome page.
- Create a new WelcomeWindow and navigate to WelcomePage. You can remove all the content inside the existing welcome page because we are doing a new welcome page.
- In App.xaml.cs, we must check if the user has accounts defined or not. If no accounts, create this new window and activate it.
- In this Window I want to highlight a few things. It should clearly say that this is a native Windows application that supports Mail and Calendar.
- Design is not important at the moment, but make sure to follow Windows fluent design guidelines.
- In this page users must be able to

+ Go to manage accounts page
+ Show the latest version changelog. We have this new "What's new" dialog, but resolve the latest json for the update and show it on the page as well using the same FlipView. You can make this a UserControl maybe. It'll be exactly the same, except "Get started" button should not be visible when the control is loaded in WelcomePage.
+ Some details from the About page like version, github page, donation link etc. would be great. We can show it here in new WelcomePage too.

