﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Server;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenIddict.Infrastructure {
    public partial class OpenIddictProvider<TUser, TApplication, TAuthorization, TScope, TToken> : OpenIdConnectServerProvider
        where TUser : class where TApplication : class where TAuthorization : class where TScope : class where TToken : class {
        public override async Task ExtractAuthorizationRequest([NotNull] ExtractAuthorizationRequestContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            // Reject requests using the unsupported request parameter.
            if (!string.IsNullOrEmpty(context.Request.GetParameter(OpenIdConnectConstants.Parameters.Request))) {
                services.Logger.LogError("The authorization request was rejected because it contained " +
                                         "an unsupported parameter: {Parameter}.", "request");

                context.Reject(
                    error: OpenIdConnectConstants.Errors.RequestNotSupported,
                    description: "The request parameter is not supported.");

                return;
            }

            // Reject requests using the unsupported request_uri parameter.
            if (!string.IsNullOrEmpty(context.Request.RequestUri)) {
                services.Logger.LogError("The authorization request was rejected because it contained " +
                                         "an unsupported parameter: {Parameter}.", "request_uri");

                context.Reject(
                    error: OpenIdConnectConstants.Errors.RequestUriNotSupported,
                    description: "The request_uri parameter is not supported.");

                return;
            }

            // If a request_id parameter can be found in the authorization request,
            // restore the complete authorization request stored in the distributed cache.
            if (!string.IsNullOrEmpty(context.Request.GetRequestId())) {
                var payload = await services.Options.Cache.GetAsync(OpenIddictConstants.Environment.Request + context.Request.GetRequestId());
                if (payload == null) {
                    services.Logger.LogError("The authorization request was rejected because an unknown " +
                                             "or invalid request_id parameter was specified.");

                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidRequest,
                        description: "Invalid request: timeout expired.");

                    return;
                }

                // Restore the authorization request parameters from the serialized payload.
                context.Request.Import(payload);
            }
        }

        public override async Task ValidateAuthorizationRequest([NotNull] ValidateAuthorizationRequestContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            // Note: the OpenID Connect server middleware supports authorization code, implicit, hybrid,
            // none and custom flows but OpenIddict uses a stricter policy rejecting unknown flows.
            if (!context.Request.IsAuthorizationCodeFlow() && !context.Request.IsHybridFlow() &&
                !context.Request.IsImplicitFlow() && !context.Request.IsNoneFlow()) {
                services.Logger.LogError("The authorization request was rejected because the '{ResponseType}' " +
                                         "response type is not supported.", context.Request.ResponseType);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    description: "The specified response_type parameter is not supported.");

                return;
            }

            // Reject code flow authorization requests if the authorization code flow is not enabled.
            if (context.Request.IsAuthorizationCodeFlow() &&
               !services.Options.GrantTypes.Contains(OpenIdConnectConstants.GrantTypes.AuthorizationCode)) {
                services.Logger.LogError("The authorization request was rejected because " +
                                         "the authorization code flow was not enabled.");

                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    description: "The specified response_type parameter is not allowed.");

                return;
            }

            // Reject implicit flow authorization requests if the implicit flow is not enabled.
            if (context.Request.IsImplicitFlow() && !services.Options.GrantTypes.Contains(OpenIdConnectConstants.GrantTypes.Implicit)) {
                services.Logger.LogError("The authorization request was rejected because the implicit flow was not enabled.");

                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    description: "The specified response_type parameter is not allowed.");

                return;
            }

            // Reject hybrid flow authorization requests if the authorization code or the implicit flows are not enabled.
            if (context.Request.IsHybridFlow() && (!services.Options.GrantTypes.Contains(OpenIdConnectConstants.GrantTypes.AuthorizationCode) ||
                                                   !services.Options.GrantTypes.Contains(OpenIdConnectConstants.GrantTypes.Implicit))) {
                services.Logger.LogError("The authorization request was rejected because the " +
                                         "authorization code flow or the implicit flow was not enabled.");

                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    description: "The specified response_type parameter is not allowed.");

                return;
            }

            // Note: the OpenID Connect server middleware supports the query, form_post and fragment response modes
            // and doesn't reject unknown/custom modes until the ApplyAuthorizationResponse event is invoked.
            // To ensure authorization requests are rejected early enough, an additional check is made by OpenIddict.
            if (!string.IsNullOrEmpty(context.Request.ResponseMode) && !context.Request.IsFormPostResponseMode() &&
                                                                       !context.Request.IsFragmentResponseMode() &&
                                                                       !context.Request.IsQueryResponseMode()) {
                services.Logger.LogError("The authorization request was rejected because the '{ResponseMode}' " +
                                         "response mode is not supported.", context.Request.ResponseMode);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The specified response_mode parameter is not supported.");

                return;
            }

            // Note: redirect_uri is not required for pure OAuth2 requests
            // but this provider uses a stricter policy making it mandatory,
            // as required by the OpenID Connect core specification.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest.
            if (string.IsNullOrEmpty(context.RedirectUri)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The required redirect_uri parameter was missing.");

                return;
            }

            // Reject authorization requests that specify scope=offline_access if the refresh token flow is not enabled.
            if (context.Request.HasScope(OpenIdConnectConstants.Scopes.OfflineAccess) &&
               !services.Options.GrantTypes.Contains(OpenIdConnectConstants.GrantTypes.RefreshToken)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The 'offline_access' scope is not allowed.");

                return;
            }

            // Retrieve the application details corresponding to the requested client_id.
            var application = await services.Applications.FindByClientIdAsync(context.ClientId);
            if (application == null) {
                services.Logger.LogError("The authorization request was rejected because the client " +
                                         "application was not found: '{ClientId}'.", context.ClientId);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Application not found in the database: ensure that your client_id is correct.");

                return;
            }

            if (!await services.Applications.ValidateRedirectUriAsync(application, context.RedirectUri)) {
                services.Logger.LogError("The authorization request was rejected because the redirect_uri " +
                                         "was invalid: '{RedirectUri}'.", context.RedirectUri);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Invalid redirect_uri.");

                return;
            }

            // To prevent downgrade attacks, ensure that authorization requests using the hybrid/implicit
            // flow are rejected if the client identifier corresponds to a confidential application.
            // Note: when using the authorization code grant, ValidateTokenRequest is responsible of
            // rejecting the token request if the client_id corresponds to an unauthenticated confidential client.
            var type = await services.Applications.GetClientTypeAsync(application);
            if (!string.Equals(type, OpenIddictConstants.ClientTypes.Public, StringComparison.OrdinalIgnoreCase) &&
                !context.Request.IsAuthorizationCodeFlow()) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "Confidential clients can only use response_type=code.");

                return;
            }

            // Ensure that the appropriate set of scopes is requested to prevent personal data leakage when possible.
            if (services.Users.SupportsUserEmail && context.HttpContext.User.Identities.Any(identity => identity.IsAuthenticated) &&
                                                    context.Request.HasScope(OpenIdConnectConstants.Scopes.Profile) &&
                                                   !context.Request.HasScope(OpenIdConnectConstants.Scopes.Email)) {
                // Skip scope validation if the user cannot be found in the database.
                var user = await services.Users.GetUserAsync(context.HttpContext.User);
                if (user == null) {
                    services.Logger.LogWarning("The authorization request was not fully validated because the profile corresponding " +
                                               "to the logged in user was not found in the database: {Identifier}.",
                                               context.HttpContext.User.GetClaim(ClaimTypes.NameIdentifier));
                }

                else {
                    // Retrieve the username and the email address associated with the user.
                    var username = await services.Users.GetUserNameAsync(user);
                    var email = await services.Users.GetEmailAsync(user);

                    // Return an error if the username corresponds to the registered
                    // email address and if the "email" scope has not been requested.
                    if (!string.IsNullOrEmpty(email) && string.Equals(username, email, StringComparison.OrdinalIgnoreCase)) {
                        services.Logger.LogError("The authorization request was rejected because the 'email' scope was not requested: " +
                                                 "to prevent data leakage, the 'email' scope must be granted when the username " +
                                                 "is identical to the email address associated with the user profile.");

                        context.Reject(
                            error: OpenIdConnectConstants.Errors.InvalidRequest,
                            description: "The 'email' scope is required.");

                        return;
                    }
                }
            }

            // Run additional checks for prompt=none requests.
            if (string.Equals(context.Request.Prompt, "none", StringComparison.Ordinal)) {
                // If the user is not authenticated, return an error to the client application.
                // See http://openid.net/specs/openid-connect-core-1_0.html#Authenticates
                if (!context.HttpContext.User.Identities.Any(identity => identity.IsAuthenticated)) {
                    services.Logger.LogError("The prompt=none authorization request was rejected because the user was not logged in.");

                    context.Reject(
                        error: OpenIdConnectConstants.Errors.LoginRequired,
                        description: "The user must be authenticated.");

                    return;
                }

                // Ensure that the authentication cookie contains the required NameIdentifier claim.
                var identifier = context.HttpContext.User.GetClaim(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(identifier)) {
                    services.Logger.LogError("The prompt=none authorization request was rejected because the user session " +
                                             "was invalid and didn't contain the mandatory ClaimTypes.NameIdentifier claim.");

                    context.Reject(
                        error: OpenIdConnectConstants.Errors.ServerError,
                        description: "The authorization request cannot be processed.");

                    return;
                }

                // Extract the principal contained in the id_token_hint parameter.
                // If no principal can be extracted, an error is returned to the client application.
                var principal = await context.HttpContext.Authentication.AuthenticateAsync(context.Options.AuthenticationScheme);
                if (principal == null) {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidRequest,
                        description: "The required id_token_hint parameter is missing.");

                    return;
                }

                // Ensure the client application is listed as a valid audience in the identity token
                // and that the identity token corresponds to the authenticated user.
                if (!principal.HasClaim(OpenIdConnectConstants.Claims.Audience, context.Request.ClientId) ||
                    !principal.HasClaim(ClaimTypes.NameIdentifier, identifier)) {
                    services.Logger.LogError("The prompt=none authorization request was rejected because the id_token_hint was invalid.");

                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidRequest,
                        description: "The id_token_hint parameter is invalid.");

                    return;
                }
            }

            context.Validate();
        }

        public override async Task HandleAuthorizationRequest([NotNull] HandleAuthorizationRequestContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            if (string.Equals(context.Request.Prompt, "none", StringComparison.Ordinal)) {
                // Note: principal is guaranteed to be non-null since ValidateAuthorizationRequest
                // rejects prompt=none requests missing or having an invalid id_token_hint.
                var principal = await context.HttpContext.Authentication.AuthenticateAsync(context.Options.AuthenticationScheme);
                Debug.Assert(principal != null, "The principal extracted from the id_token_hint shouldn't be null.");

                // Note: user may be null if the user was removed after
                // the initial check made by ValidateAuthorizationRequest.
                var user = await services.Users.GetUserAsync(principal);
                if (user == null) {
                    services.Logger.LogError("The authorization request was aborted because the profile corresponding " +
                                             "to the logged in user was not found in the database: {Identifier}.",
                                             context.HttpContext.User.GetClaim(ClaimTypes.NameIdentifier));

                    context.Reject(
                        error: OpenIdConnectConstants.Errors.ServerError,
                        description: "An internal error has occurred.");

                    return;
                }

                // Note: filtering the username is not needed at this stage as OpenIddictController.Accept
                // and OpenIddictProvider.HandleTokenRequest are expected to reject requests that don't
                // include the "email" scope if the username corresponds to the registed email address.
                var identity = await services.Users.CreateIdentityAsync(user, context.Request.GetScopes());
                if (identity == null) {
                    throw new InvalidOperationException("The authorization request was aborted because the user manager returned a null " +
                                                       $"identity for user '{await services.Users.GetUserNameAsync(user)}'.");
                }

                // Create a new authentication ticket holding the user identity.
                var ticket = new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    new AuthenticationProperties(),
                    context.Options.AuthenticationScheme);

                ticket.SetResources(context.Request.GetResources());
                ticket.SetScopes(context.Request.GetScopes());

                // Call SignInAsync to create and return a new OpenID Connect response containing the serialized code/tokens.
                await context.HttpContext.Authentication.SignInAsync(ticket.AuthenticationScheme, ticket.Principal, ticket.Properties);

                // Mark the response as handled
                // to skip the rest of the pipeline.
                context.HandleResponse();

                return;
            }

            // If no request_id parameter can be found in the current request, assume the OpenID Connect request
            // was not serialized yet and store the entire payload in the distributed cache to make it easier
            // to flow across requests and internal/external authentication/registration workflows.
            if (string.IsNullOrEmpty(context.Request.GetRequestId())) {
                // Generate a request identifier. Note: using a crypto-secure
                // random number generator is not necessary in this case.
                var identifier = Guid.NewGuid().ToString();

                var options = new DistributedCacheEntryOptions {
                    AbsoluteExpiration = context.Options.SystemClock.UtcNow + TimeSpan.FromMinutes(30),
                    SlidingExpiration = TimeSpan.FromMinutes(10)
                };

                // Store the serialized authorization request parameters in the distributed cache.
                await services.Options.Cache.SetAsync(OpenIddictConstants.Environment.Request + identifier, context.Request.Export(), options);

                // Create a new authorization request containing only the request_id parameter.
                var address = QueryHelpers.AddQueryString(
                    uri: context.HttpContext.Request.PathBase + context.HttpContext.Request.Path,
                    name: OpenIdConnectConstants.Parameters.RequestId, value: identifier);

                context.HttpContext.Response.Redirect(address);

                // Mark the response as handled
                // to skip the rest of the pipeline.
                context.HandleResponse();

                return;
            }

            context.SkipToNextMiddleware();
        }

        public override async Task ApplyAuthorizationResponse([NotNull] ApplyAuthorizationResponseContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            // Remove the authorization request from the distributed cache.
            if (!string.IsNullOrEmpty(context.Request.GetRequestId())) {
                // Note: the ApplyAuthorizationResponse event is called for both successful
                // and errored authorization responses but discrimination is not necessary here,
                // as the authorization request must be removed from the distributed cache in both cases.
                await services.Options.Cache.RemoveAsync(OpenIddictConstants.Environment.Request + context.Request.GetRequestId());
            }

            if (!context.Options.ApplicationCanDisplayErrors && !string.IsNullOrEmpty(context.Response.Error) &&
                                                                 string.IsNullOrEmpty(context.Response.RedirectUri)) {
                // Determine if the status code pages middleware has been enabled for this request.
                // If it was not registered or disabled, let the OpenID Connect server middleware render
                // a default error page instead of delegating the rendering to the status code middleware.
                var feature = context.HttpContext.Features.Get<IStatusCodePagesFeature>();
                if (feature != null && feature.Enabled) {
                    // Replace the default status code to return a 400 response.
                    context.HttpContext.Response.StatusCode = 400;

                    // Store the OpenID Connect response in the HTTP context to allow retrieving it
                    // from user code (e.g from an ASP.NET Core MVC controller or a Nancy module).
                    context.HttpContext.SetOpenIdConnectResponse(context.Response);

                    // Mark the request as fully handled to prevent the OpenID Connect server middleware
                    // from displaying the default error page and to allow the status code pages middleware
                    // to rewrite the response using the logic defined by the developer when registering it.
                    context.HandleResponse();
                }
            }
        }
    }
}