namespace IPRO.Entities;

// 585: the content hash (SHA-256, lower-case hex, as the importer writes it) of each starter banner the page editor offers. The new
// builder (Beta) offers the same pictures; one that comes back from it is recognised by its bytes and
// linked to the shared banner, as the page editor links it, instead of being stored again against the
// adviser's storage. BuilderBeta585Tests (Every_starter_banner_is_known_by_its_content) holds this list
// to the files themselves.
public static class WebsiteStarterBannerHashes
{
    public static IReadOnlyDictionary<string, string> FileBySha256 { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["8d43afd4aaa2091f3dfef4e0e8e9bd0a12c005b3f398e327264cafd25c25f6fc"] = "201042010145233395.jpg",
        ["4e1e8c1b70c2b86eb770c5e9863de4101b5be9b891d887f2fe7ef01ada96e850"] = "20104201094951770.jpg",
        ["dfaf69a272ffafd315b84accdf86ab0cceb1c4640415d826bef6e196268b7370"] = "201062010115428234.jpg",
        ["8c04b1bbab6cef619d4b417e2ab803d4b828e3701cd024ec2d079d0ed1e6b87d"] = "201062010133625656.jpg",
        ["648e6572c8becebc340510ed41bb3c352adfec8c0a0f55e8de4dcd27073e3053"] = "201062010133643250.jpg",
        ["8c8185fe9c876945cb2e938270c8ef93f7d5f5952b34074cb9c879632685b4ea"] = "201062010133724296.jpg",
        ["775a682660ad1ef8e01c8f9234d9cbae6774ecb22f296ed3897ef0eb90282164"] = "5header-image.jpg",
        ["2e13e6b35b35f5600ad5d4086a2e288ae0fba184217fe5f0622858ab31dfe40f"] = "agent_banner.jpg",
        ["8e33adeb2f1f77ec17b82f5acfa943d3c05e0c080153364684668b7d821ce964"] = "agent_banner5.jpg",
        ["26ae06512a1c30e1dbd068fa7349675abcff4bbd7626c4877802e8733adc8de8"] = "allfamily.jpg",
        ["af8dae75ba37caca5505132a947b2c42d8964b427efe14fea2414161bd40dea8"] = "family3grass.jpg",
        ["8777e0fcbbe36556f87c4e28af435ea1e80813faf8b605a40b5ebf72f8b7ceba"] = "familybaby.jpg",
        ["fbb461a18e6132d8a3a13d58321e6e38845438026a146a40bd008aface4c4009"] = "familychinese.jpg",
        ["2242e78ed7676437c055780213b0b82ff38a272f726e996f5425ae1af240d055"] = "sunset.jpg",
        ["bd955f868617f701df9d9b7a91b35864ea971748eb38e6ef6f04114b0981d16a"] = "top_banner_agent_r4.jpg",
        ["9eaa7512e06009ae1e3b7db6eaf3508586afc2688cfd0a61cd5515350d2c2199"] = "agreed.jpg",
        ["f5b97312e8808c6f798ea3a35d2de490a0003f3eedc65197fc7931ebf01ae25b"] = "building1.jpg",
        ["82293e0460a7273df9624b6e4fb6f939320df7cbdde99c845adfcac31a56f2bf"] = "building2.jpg",
        ["5f3863f46482d433a2cb43798294e9121fa5f1bf907eb1443c13bbdcd45eb1b0"] = "building3.jpg",
        ["e05696e16a82e95dffb43746a4af35bcc4471c1de666d06408eb2e6551aed86c"] = "open.jpg",
        ["bb90875b05f61a024a718623d80e59fe3434930dfd894b14cc9cadc885bf3419"] = "puzzle.jpg",
        ["d0d4176428ee8474cbc1b142e13abfa9b38e86ca7d05885aefad35cc2418f3cf"] = "results.jpg",
        ["279b61c377ac520cfe229500b724985f780bf3a071ae8f498a69a829f7e5945c"] = "right_wrong.jpg",
        ["e37bd8758e2593639b5d7f9208aad9a55d01f081729729fc4795b367c0af8918"] = "thumbup.jpg",
        ["01d383acb5568232e89082dd809ae172c2ff281ccab3b52fa0ab577c59a6adf2"] = "people-silhouettes.jpg",
        ["902b6c8a2afe57a16aac9e650dcc0f30017138b978786c39e3a5c74a9cfca77f"] = "getinsurance.jpg",
        ["7f75944950212e7544f895592e4cef7a1254672a035c7bc47606f0ab9b9e9847"] = "security-camera.jpg",
        ["1f64b454b746a5d4bed0d9641bdb7434959d55f0e38b66671820ec0e8636c689"] = "keeping-watch.jpg",
        ["c031b950837bef2b2543ad9903d195f59f3675330360eaa00000a924ef4a1da8"] = "banner.jpg",
        ["84610ac609a0338ccf57d55ebc80c3f2e19e72ecb611ce77fcac8fd078f7ab57"] = "globe_people.jpg",
        ["4c7f96758761bc4234877ce05544e012afae9fd3c26ac86f12c555912319e8ab"] = "hands.jpg",
        ["a1d0d8de02e37a0b7d0f73daeb297455493499dd37d3ab6bf33566dec5f5e636"] = "listening.jpg",
        ["1fd4f599d77eb50cc7ade814b4790c367d92c0a51b2c1d12ea1adbf18c2ad3af"] = "global-vision.jpg",
        ["d64412f48212efebaff3c2b34a295ea79fad364613a2af85e06a6a523ac0a7b3"] = "angel-statue.jpg",
        ["bb8b1f6800dbb40f32ee4a539fea053ae02e672dead6fcc1f8a04bfb99369b1c"] = "blue-globe.jpg",
        ["67896a3ea67c37b1ec62847701afb54e3863dc2867e97bd69915778da8f71953"] = "glass-marble.jpg",
        ["7374673b8cc76c605ce67ddf836c84fc85562260f58821b635ea4e4bb2575e69"] = "autumn-valley.jpg",
        ["37b87fd6c496020064a8a028da82b1a6e46233804a8b5b68392ae3ab7d37258c"] = "mountain-range.jpg",
        ["03f7fb4ec3b838b83c4239178a6266322ee49ed28090d159cc6c53502f0af2ac"] = "sandy-beach.jpg",
        ["736207c32f768a544adac6ca3d18fd0253bf88ec52330fba2dae2c5cbd1f367d"] = "blue-sky.jpg",
        ["f069d9b816f1eb01c419f18aceaefb6d6cf2a84e0571d3982e47037b909f9db8"] = "summer-clouds.jpg",
        ["a9a713af7ed3a91b7c62e5f2b64f623e7225501ef7beaef6dc663b97cbf76651"] = "niagara-falls.jpg",
        ["ec191fdb7f1f7109e4a336f8638688931d43b16e9014aa0b503075ffb8cda400"] = "frozen-shoreline.jpg",
        ["fbd42c8db52295fc41953f6eec0270d001728570b2ce785973046f3d059a7cb4"] = "grand-canyon.jpg",
        ["ec39a54427c720cd709326d9c63da969ad8db763715f1db8a7baa2988f2f2c36"] = "canyon-horizon.jpg",
        ["526c3a5523de1cec38450857242dcdb7cfe1620e5659adea9f4d541c19130f34"] = "above-the-clouds.jpg",
        ["58ce41b9b1c03aabf90074bcaf3c1ac3bda677c66e422a2df4bd7b3014faf0c5"] = "whale-off-the-coast.jpg",
        ["ad51765b0b44575dda5027f85efe96a629ee448cb9548c4c42ec3e6403968cc0"] = "country-road.jpg",
        ["e41dec366c910a5d65a82f74997102908b7cc0aa7531dd309b4e43a4a4a4affa"] = "pebbles.jpg",
        ["e4a23c15dafda38f02683fcf7e2ba9bc35d720195fece62d317644aa9b3ee4b3"] = "toronto-skyline.jpg",
        ["cbda8acc8b13fd5298cdf2dc1437f3a9d6f30ad767478a4703412a9cdfab97ab"] = "toronto-harbour.jpg",
        ["cab677e4e32df4d0a677a56da87f290e373b64056d96f2b5cf7e2bd1c53f9d39"] = "old-city-rooftops.jpg",
        ["a2be7a91c4663b552896f5c186c395412b536744b5a3f0ce064dfaf8215046be"] = "colourful-houses.jpg",
        ["5fef9a1a776bb37624cc117864ef695957acbb76389fbbfbd4ad929ebea6fd59"] = "working-harbour.jpg",
        ["7ec2d3a387f2beb3230fcc16a0aa227727bb7d17773eb2a61dc20112a19ad75e"] = "stairway.jpg",
        ["f66f835857d4aaf7af14142dde0a926f406f00499121abe4a0af900dc4db7adb"] = "colourful-bricks.jpg",
    };

    // The address the page editor gives this banner, or null when the bytes are not one of them.
    public static string? UrlFor(string sha256) =>
        FileBySha256.TryGetValue(sha256, out var file) ? WebsiteStarterBannerCatalog.All.FirstOrDefault(b => b.FileName == file)?.Url : null;
}
