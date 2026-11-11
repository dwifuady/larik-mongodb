using Larik.Data;
using Larik.Data.MongoDB;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Profile → <see cref="MongoClientSettings"/> tests. No server is contacted: they pin down how a
/// pasted Atlas URI is interpreted, which is where the field-only model used to fail.
/// </summary>
public sealed class MongoConnectionSettingsTests
{
    private const string AtlasUri =
        "mongodb+srv://app_user:s3cret@cluster0.abcd1.mongodb.net/sample_db?retryWrites=true&w=majority";

    private static ConnectionProfile Profile(params (string Key, string Value)[] values)
    {
        var profile = new ConnectionProfile { ProviderId = MongoProvider.ProviderId };
        foreach (var (key, value) in values)
        {
            profile.Values[key] = value;
        }

        return profile;
    }

    [Fact]
    public void Srv_connection_string_discovers_the_cluster_instead_of_dialling_one_node()
    {
        var (settings, database) = MongoConnection.Build(Profile(
            ("connectionString", AtlasUri),
            ("connectTimeout", "7")));

        Assert.Equal(ConnectionStringScheme.MongoDBPlusSrv, settings.Scheme);
        Assert.False(settings.DirectConnection);
        Assert.Equal("cluster0.abcd1.mongodb.net", settings.Server.Host);
        Assert.Equal("app_user", settings.Credential?.Username);
        Assert.Equal("sample_db", database);
        Assert.Equal(TimeSpan.FromSeconds(7), settings.ServerSelectionTimeout);
    }

    [Fact]
    public void Connection_string_wins_over_the_discrete_fields()
    {
        var (settings, database) = MongoConnection.Build(Profile(
            ("connectionString", AtlasUri),
            ("host", "ignored.example.com"),
            ("user", "ignored"),
            ("password", "ignored")));

        Assert.Equal("cluster0.abcd1.mongodb.net", settings.Server.Host);
        Assert.Equal("app_user", settings.Credential?.Username);
        Assert.Equal("sample_db", database);
    }

    [Fact]
    public void A_uri_without_user_info_still_takes_the_discrete_credential()
    {
        var (settings, database) = MongoConnection.Build(Profile(
            ("connectionString", "mongodb+srv://cluster0.abcd1.mongodb.net/sample_db"),
            ("user", "app_user"),
            ("password", "s3cret")));

        Assert.Equal("app_user", settings.Credential?.Username);
        Assert.Equal("admin", settings.Credential?.Source);
        Assert.Equal("sample_db", database);
        Assert.Equal(
            "app_user",
            new MongoProvider().DescribeIdentity(Profile(
                ("connectionString", "mongodb+srv://cluster0.abcd1.mongodb.net/sample_db"),
                ("user", "app_user"))));
    }

    [Fact]
    public void A_uri_that_names_the_user_but_not_the_password_takes_it_from_the_field()
    {
        var (settings, _) = MongoConnection.Build(Profile(
            ("connectionString", "mongodb+srv://app_user@cluster0.abcd1.mongodb.net/sample_db?authSource=authdb"),
            ("password", "s3cret")));

        Assert.Equal("app_user", settings.Credential?.Username);
        Assert.Equal("authdb", settings.Credential?.Source);
    }

    [Fact]
    public void Standard_uri_keeps_every_seed_and_its_options()
    {
        var (settings, database) = MongoConnection.Build(Profile(
            ("connectionString", "mongodb://app_user:pw@h1.example.com:27017,h2.example.com:27018/shop?authSource=admin&replicaSet=rs0&tls=true")));

        Assert.Equal(ConnectionStringScheme.MongoDB, settings.Scheme);
        Assert.Equal(
            ["h1.example.com:27017", "h2.example.com:27018"],
            settings.Servers.Select(server => server.ToString()).ToArray());
        Assert.True(settings.UseTls);
        Assert.Equal("admin", settings.Credential?.Source);
        Assert.Equal("shop", database);
    }

    [Fact]
    public void Host_accepts_an_inline_port_and_prefers_it_over_the_port_field()
    {
        var (settings, database) = MongoConnection.Build(Profile(
            ("host", " db.example.com:27018 "),
            ("port", "27017")));

        Assert.Equal("db.example.com", settings.Server.Host);
        Assert.Equal(27018, settings.Server.Port);
        Assert.Null(database);
    }

    [Fact]
    public void A_host_without_a_port_uses_the_port_field()
    {
        var (settings, _) = MongoConnection.Build(Profile(
            ("host", "db.example.com"),
            ("port", "27019")));

        Assert.Equal(27019, settings.Server.Port);
    }

    [Fact]
    public void A_missing_host_and_connection_string_explains_both_paths()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MongoConnection.Build(Profile()));

        Assert.Contains("host name", error.Message);
        Assert.Contains("connection string", error.Message);
    }

    [Fact]
    public void A_non_uri_connection_string_reports_the_scheme_not_the_driver_wording()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => MongoConnection.Build(Profile(("connectionString", "not a uri at all"))));

        Assert.Contains("mongodb://", error.Message);
        Assert.DoesNotContain("is not a valid host", error.Message);
    }

    [Fact]
    public void A_uri_pasted_into_host_points_at_the_connection_string_field_without_echoing_the_secret()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MongoConnection.Build(Profile(
            ("host", "mongodb+srv://app_user:s3cret@cluster0.abcd1.mongodb.net/sample_db"))));

        Assert.Contains("Connection string", error.Message);
        Assert.DoesNotContain("s3cret", error.Message);
    }

    [Fact]
    public void Endpoint_hides_the_password_and_the_srv_dns_port()
    {
        var provider = new MongoProvider();

        var endpoint = provider.DescribeEndpoint(Profile(("connectionString", AtlasUri)));

        Assert.Equal("MongoDB · cluster0.abcd1.mongodb.net", endpoint);
        Assert.DoesNotContain("s3cret", endpoint);
        Assert.Equal("app_user", provider.DescribeIdentity(Profile(("connectionString", AtlasUri))));
    }

    [Fact]
    public void A_half_typed_connection_string_falls_back_to_the_host_fields()
    {
        var provider = new MongoProvider();
        var profile = Profile(
            ("connectionString", "not a uri at all"),
            ("host", "db.example.com"),
            ("port", "27017"));

        Assert.Equal("MongoDB · db.example.com:27017", provider.DescribeEndpoint(profile));
    }
}
