namespace IPRO.Entities;

// 665 (2026-11-20): one rule for "a page of this site", for every link an adviser types (header and block
// buttons, menu links, the announcement bar, the pop-up's buttons, newsletter sidebar buttons). A path
// starts with '/', but "//other-site" and "/\other-site" leave the site: browsers read a backslash as a
// slash, and they drop tabs and line breaks inside an address, so "/<tab>/other-site" leaves it too.
// Before this, ButtonLinks kept anything starting with '/', and the pages' NormalizeUrl copies refused
// "//" but not "/\".
public static class SitePaths
{
    public static bool IsOwnPage(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/') return false;
        if (value.Length > 1 && value[1] == '/') return false;
        foreach (var c in value)
        {
            if (c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        return true;
    }
}
