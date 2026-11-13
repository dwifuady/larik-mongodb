using Larik.Data.MongoDB;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The read gate an agent passes on a workspace exposed read-only. Each case names the behaviour an
/// agent depends on, not the scanner's internals: a read runs, everything else is refused.
/// </summary>
public sealed class MongoReadOnlyPolicyTests
{
    private static readonly MongoReadOnlyPolicy Policy = MongoReadOnlyPolicy.Instance;

    [Theory]
    [InlineData("SELECT * FROM [users] WHERE [age] > 18")]
    [InlineData("SELECT TOP 10 [name] FROM [users];")]
    [InlineData("SELECT * FROM [users]; USE [shop]")]
    [InlineData("USE [shop]")]
    [InlineData("-- the open orders\nSELECT * FROM [orders] WHERE [status] = 'open'")]
    [InlineData("db.users.find({})")]
    [InlineData("db.users.find({}, { name: 1 })")]
    [InlineData("db.users.find({}).sort({ age: -1 }).limit(5)")]
    [InlineData("db.users.find({}).skip(10).limit(5)")]
    [InlineData("db.users.find({}).project({ name: 1 })")]
    [InlineData("SELECT [city], COUNT(*) AS [n] FROM [users] GROUP BY [city] ORDER BY [n] DESC")]
    [InlineData("db.users.findOne({ _id: 1 })")]
    [InlineData("db.users.find({ name: 'a;b' });")]
    [InlineData("db.orders.count({ status: 'open' })")]
    [InlineData("db.orders.countDocuments({ status: 'open' })")]
    [InlineData("db.orders.estimatedDocumentCount()")]
    [InlineData("db.users.distinct('city')")]
    [InlineData("db.users.getIndexes()")]
    [InlineData("db.users.stats()")]
    [InlineData("db.getCollectionNames()")]
    [InlineData("db.stats()")]
    public void Reads_are_proven(string statement) => Assert.True(Policy.IsProvenRead(statement));

    [Theory]
    [InlineData("INSERT INTO [users] ([name]) VALUES ('x')")]
    [InlineData("UPDATE [users] SET [name] = 'x' WHERE [id] = 1")]
    [InlineData("DELETE FROM [users] WHERE [id] = 1")]
    [InlineData("DROP TABLE [users]")]
    [InlineData("db.users.insertOne({ a: 1 })")]
    [InlineData("db.users.insertMany([{ a: 1 }])")]
    [InlineData("db.users.updateMany({}, { $set: { a: 1 } })")]
    [InlineData("db.users.deleteMany({})")]
    [InlineData("db.runCommand({ dropDatabase: 1 })")]
    [InlineData("db.getSiblingDB('admin').adminCommand({ shutdown: 1 })")]
    // A read method is only valid at its own level.
    [InlineData("db.getIndexes()")]
    [InlineData("db.users.getCollectionNames()")]
    [InlineData("db.find({})")]
    // aggregate is refused outright: it can write through $out/$merge, and a text scan cannot prove
    // a parsed pipeline is read-only (an escaped \u0024out is still $out to the parser).
    [InlineData("db.orders.aggregate([{ $match: { status: 'open' } }, { $limit: 10 }])")]
    [InlineData("db.users.aggregate([{ $match: {} }, { $out: 'users' }])")]
    [InlineData("db.users.aggregate([{ $merge: { into: 'archive' } }])")]
    [InlineData("db.users.aggregate([{ \"$out\": \"users\" }])")]
    [InlineData("db.users.aggregate([{ \"\\u0024out\": \"users\" }])")]
    [InlineData("db.users.aggregate([{ $facet: { a: [{ $out: 'x' }] } }])")]
    [InlineData("db.users.find({}, { $out: 1 })")]
    // Only the cursor modifiers that narrow a find are chainable; a callback can write.
    [InlineData("db.users.find({}).forEach(function (u) { db.users.deleteOne({ _id: u._id }); })")]
    [InlineData("db.users.find({}).deleteMany({})")]
    [InlineData("db.users.count({}).limit(5)")]
    [InlineData("db.users.find({}).limit(-1)")]
    // One non-read unit poisons the batch, even when the first statement reads.
    [InlineData("SELECT * FROM [users]; db.users.deleteMany({})")]
    [InlineData("db.users.find({}); db.users.insertOne({ a: 1 })")]
    [InlineData("select * from")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void Anything_else_is_refused(string statement) => Assert.False(Policy.IsProvenRead(statement));

    [Fact]
    public void Provider_offers_the_policy()
    {
        // The host refuses read-mode execution outright for a provider that returns null here, which
        // is exactly what an agent saw before this gate existed.
        Assert.NotNull(new MongoProvider().ReadOnlyPolicy);
    }
}
