using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Qobuz.API;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>
    /// Validates Qobuz indexer settings.
    /// </summary>
    /// <remarks>
    /// This validator was previously empty, so credentials that could not possibly work saved
    /// without complaint. The indexer then returned a null request generator, which the host
    /// turned into an unexplained "Unable to connect to indexer" and silently empty searches.
    /// Catching it here names the actual problem on the actual field.
    /// </remarks>
    public class QobuzIndexerSettingsValidator : AbstractValidator<QobuzIndexerSettings>
    {
        public QobuzIndexerSettingsValidator()
        {
            RuleFor(x => x)
                .Must(x => x.ToCredentials().IsComplete)
                .WithMessage(
                    "Supply either an email and MD5 password, or a user id and auth token. "
                    + "The password must be an MD5 hash, not the password itself.")
                .WithName("Authentication");

            // Qobuz app credentials are optional, but useless one at a time.
            RuleFor(x => x.AppSecret)
                .NotEmpty()
                .WithMessage("An App Secret is required when an App ID is supplied.")
                .When(x => !string.IsNullOrWhiteSpace(x.AppID));

            RuleFor(x => x.AppID)
                .NotEmpty()
                .WithMessage("An App ID is required when an App Secret is supplied.")
                .When(x => !string.IsNullOrWhiteSpace(x.AppSecret));

            RuleFor(x => x.EarlyReleaseLimit)
                .GreaterThan(0)
                .When(x => x.EarlyReleaseLimit.HasValue)
                .WithMessage("Early Download Limit must be greater than zero, or empty.");
        }
    }

    public class QobuzIndexerSettings : IIndexerSettings
    {
        private static readonly QobuzIndexerSettingsValidator Validator = new QobuzIndexerSettingsValidator();

        [FieldDefinition(0, Label = "Qobuz Email", Type = FieldType.Textbox, HelpTextWarning = "If an email+password and an id+token are supplied at the same time, the email/password will be used. Only one form of authentication is needed.")]
        public string Email { get; set; } = "";

        [FieldDefinition(1, Label = "Qobuz Password (MD5)", Type = FieldType.Textbox)]
        public string MD5Password { get; set; } = "";

        [FieldDefinition(2, Label = "User ID", Type = FieldType.Textbox)]
        public string UserID { get; set; } = "";

        [FieldDefinition(3, Label = "User Auth Token", Type = FieldType.Textbox, HelpTextWarning = "If a token sign in is failing, try changing the App ID and Secret.", HelpLink = "https://telegra.ph/How-to-fix-403---Invalid-usernameemail-and-password-10-12")]
        public string UserAuthToken { get; set; } = "";

        [FieldDefinition(4, Label = "App ID", Type = FieldType.Textbox, Placeholder = "Optional")]
        public string AppID { get; set; } = "";

        [FieldDefinition(5, Label = "App Secret", Type = FieldType.Textbox, Placeholder = "Optional")]
        public string AppSecret { get; set; } = "";

        [FieldDefinition(6, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        /// <summary>
        /// Required by <see cref="IIndexerSettings"/> but unused: the Qobuz API endpoint is
        /// fixed, so there is nothing for a user to configure.
        /// </summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>
        /// Takes an immutable copy of the credentials in these settings.
        /// </summary>
        /// <remarks>
        /// Always snapshot before handing credentials to another module. Lidarr reassigns
        /// <c>Definition</c> on its shared provider singletons, so reading these properties
        /// later in a call can observe a different configured indexer's values.
        /// </remarks>
        public QobuzCredentials ToCredentials() => new QobuzCredentials(
            Email,
            MD5Password,
            UserID,
            UserAuthToken,
            AppID,
            AppSecret);

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
