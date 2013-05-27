using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using MonoTouch.Accounts;
using MonoTouch.Foundation;
using MonoTouch.Social;
using MonoTouch.UIKit;
using Xamarin.Auth;

namespace Xamarin.Social.Services
{
	public abstract class SocialService : Service
	{
		private SLServiceKind kind;
		private NSString accountTypeIdentifier;

		public SocialService (string serviceId, string title, SLServiceKind kind, NSString accountTypeIdentifier)
			: base (serviceId, title)
		{
			this.kind = kind;
			this.accountTypeIdentifier = accountTypeIdentifier;
		}

		public override Task<string> GetOAuthTokenAsync (Account acc)
		{
			var tcs = new TaskCompletionSource<string> ();
			var wrapper = (ACAccountWrapper) acc;
			var credential = wrapper.ACAccount.Credential;

			if (credential != null)
				tcs.SetResult (credential.OAuthToken);
			else
				tcs.SetException (new Exception ("No credential is stored for this account."));

			return tcs.Task;
		}

		#region Share

		public override UIViewController GetShareUI (Item item, Action<ShareResult> completionHandler)
		{
			//
			// Get the native UI
			//
			var vc = SLComposeViewController.FromService (kind);

			vc.CompletionHandler = (result) => {
				var shareResult = result == SLComposeViewControllerResult.Done ? ShareResult.Done : ShareResult.Cancelled;
				completionHandler (shareResult);
			};

			vc.SetInitialText (item.Text);

			foreach (var image in item.Images) {
				vc.AddImage (image.Image);
			}

			foreach (var link in item.Links) {
				vc.AddUrl (new NSUrl (link.AbsoluteUri));
			}

			return vc;
		}

		public override Task ShareItemAsync (Item item, Account account, CancellationToken cancellationToken)
		{
			throw new NotSupportedException ("Sharing items without a GUI is not supported. Please use GetShareUI instead.");
		}

		#endregion


		#region Low-level Requests

		class SocialRequest : Request
		{
			SLRequest request;

			public SocialRequest (SLServiceKind kind, string method, Uri url, IDictionary<string, string> parametrs, Account account)
				: base (method, url, parametrs, account)
			{
				var ps = new NSMutableDictionary ();
				if (parametrs != null) {
					foreach (var p in parametrs) {
						ps.SetValueForKey (new NSString (p.Value), new NSString (p.Key));
					}
				}

				var m = SLRequestMethod.Get;
				switch (method.ToLowerInvariant()) {
					case "get":
					m = SLRequestMethod.Get;
					break;
					case "post":
					m = SLRequestMethod.Post;
					break;
					case "delete":
					m = SLRequestMethod.Delete;
					break;
					default:
					throw new NotSupportedException ("Social framework does not support the HTTP method '" + method + "'");
				}

				request = SLRequest.Create (kind, m, new NSUrl (url.AbsoluteUri), ps);

				Account = account;
			}

			public override Account Account {
				get {
					return base.Account;
				}
				set {
					base.Account = value; 

					if (request != null) {
						if (value == null) {
							// Don't do anything, not supported
						}
						else if (value is ACAccountWrapper) {
							request.Account = ((ACAccountWrapper)value).ACAccount;
						}
						else {
							throw new NotSupportedException ("Account type '" + value.GetType().FullName + "'not supported");
						}
					}
				}
			}

			public override void AddMultipartData (string name, System.IO.Stream data, string mimeType, string filename)
			{
				request.AddMultipartData (NSData.FromStream (data), name, mimeType, string.Empty);
			}

			public override Task<Response> GetResponseAsync (CancellationToken cancellationToken)
			{
				var completedEvent = new ManualResetEvent (false);

				NSError error = null;
				Response response = null;

				request.PerformRequest ((resposeData, urlResponse, err) => {
					error = err;
					response = new FoundationResponse (resposeData, urlResponse);
					completedEvent.Set ();
				});

				return Task.Factory.StartNew (delegate {
					completedEvent.WaitOne ();
					if (error != null) {
						throw new Exception (error.LocalizedDescription);
					}
					return response;
				}, TaskCreationOptions.LongRunning, cancellationToken);
			}
		}

		public override Request CreateRequest (string method, Uri url, IDictionary<string, string> paramters, Account account)
		{
			return new SocialRequest (kind, method, url, paramters, account);
		}

		#endregion


		#region Authentication

		ACAccountStore accountStore; // Save this reference since ACAccounts are only good so long as it's alive

		protected virtual AccountStoreOptions AccountStoreOptions {
			get { return null; }
		}

		public override Task<IEnumerable<Account>> GetAccountsAsync ()
		{
			if (accountStore == null) {
				accountStore = new ACAccountStore ();
			}
			var store = new ACAccountStore ();
			var at = store.FindAccountType (this.accountTypeIdentifier);

			var r = new List<Account> ();

			var completedEvent = new ManualResetEvent (false);

			store.RequestAccess (at, AccountStoreOptions, (granted, error) => {
				if (granted) {
					var accounts = store.FindAccounts (at);
					foreach (var a in accounts) {
						r.Add (new ACAccountWrapper (a, store));
					}
				}
				completedEvent.Set ();
			});

			return Task.Factory.StartNew (delegate {
				completedEvent.WaitOne ();
				return (IEnumerable<Account>)r;
			}, TaskCreationOptions.LongRunning);
		}

		public override bool SupportsAuthentication
		{
			get {
				return false;
			}
		}

		protected override Authenticator GetAuthenticator ()
		{
			throw new NotSupportedException ("This service does support authenticating users. You should direct them to the Settings application.");
		}

		#endregion
	}
}