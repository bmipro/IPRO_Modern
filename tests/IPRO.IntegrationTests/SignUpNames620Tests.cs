using IPRO.Utility;
using Xunit;

namespace IPRO.IntegrationTests;

// 620: a new account's user name and temporary site address are made from the person's name. Accented letters
// were dropped, so a French-speaking adviser's first sight of the product was a name with holes in it.
public class SignUpNames620Tests
{
    [Theory]
    [InlineData("HélèneCôté", "HeleneCote")]
    [InlineData("FrançoisLefèvre", "FrancoisLefevre")]
    [InlineData("ZoëMüller", "ZoeMuller")]
    [InlineData("JoséNúñez", "JoseNunez")]
    [InlineData("MaëlleBœuf", "MaelleBoeuf")]
    [InlineData("ÆthelSørenGroß", "AEthelSorenGross")]
    [InlineData("Mary-Anne O'Brien", "MaryAnneOBrien")]
    [InlineData("JohnSmith", "JohnSmith")]
    [InlineData("Agent 007", "Agent007")]
    public void A_name_keeps_its_letters(string name, string expected) =>
        Assert.Equal(expected, SignUpIdentifier.From(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("王小明")]   // no plain letters at all: the sign-up names the account "agent", as it always did
    public void A_name_with_no_plain_letters_gives_nothing(string? name) =>
        Assert.Equal("", SignUpIdentifier.From(name));

    [Fact]
    public void The_same_name_again_still_takes_the_next_number_and_the_sign_up_uses_these_names()
    {
        var controller = Lock583.Read(@"src\IPRO.Web\Controllers\AccountController.cs");
        Assert.Contains("private static string NormalizeIdentifier(string value) => SignUpIdentifier.From(value);", controller);
        Assert.Contains("if (string.IsNullOrWhiteSpace(baseName)) baseName = \"agent\";", controller);
        Assert.Contains("candidate = $\"{baseName}{suffix}\";", controller);
        Assert.Contains("candidate = $\"{baseName}{suffix}.247advisers.com\";", controller);
    }
}
