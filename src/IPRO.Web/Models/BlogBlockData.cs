using IPRO.Entities;

namespace IPRO.Web.Models;

// What a Blog block needs to render: either the list of recent posts, or one post in full when the
// visitor followed a "Read more" link (`?post=<id>` on the same page).
public class BlogBlockData
{
    // Newest first, already limited to the block's PostCount.
    public List<Article> Posts { get; set; } = new();

    // Set only when ?post= names one of THIS agent's published articles. When set the block renders
    // the single post and a link back to the list; otherwise it renders the list.
    public Article? SelectedPost { get; set; }

    public bool ShowImages { get; set; } = true;

    // True when the agent has published nothing yet. The block then renders a quiet placeholder
    // rather than an empty section, so a live site never shows a bare heading with nothing under it.
    public bool IsEmpty => Posts.Count == 0 && SelectedPost == null;
}
