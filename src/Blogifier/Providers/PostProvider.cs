using Blogifier.Blogs;
using Blogifier.Data;
using Blogifier.Extensions;
using Blogifier.Shared;
using Blogifier.Shared.Extensions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Blogifier.Providers;

public class PostProvider
{
  private readonly AppDbContext _db;
  private readonly CategoryProvider _categoryProvider;

  public PostProvider(AppDbContext db, CategoryProvider categoryProvider)
  {
    _db = db;
    _categoryProvider = categoryProvider;
  }

  public async Task<List<Post>> GetPosts(PublishedStatus filter, PostType postType)
  {
    var query = _db.Posts.AsNoTracking()
      .Where(p => p.PostType == postType);

    query = filter switch
    {
      PublishedStatus.Published => query.Where(p => p.State == PostState.Release).OrderByDescending(p => p.PublishedAt),
      PublishedStatus.Drafts => query.Where(p => p.PublishedAt == DateTime.MinValue).OrderByDescending(p => p.Id),
      PublishedStatus.Featured => query.Where(p => p.IsFeatured).OrderByDescending(p => p.Id),
      _ => query.OrderByDescending(p => p.Id),
    };

    return await query.ToListAsync();
  }

  public async Task<List<Post>> SearchPosts(string term)
  {
    if (term == "*")
      return await _db.Posts.ToListAsync();

    return await _db.Posts
        .AsNoTracking()
        .Where(p => p.Title.ToLower().Contains(term.ToLower()))
        .ToListAsync();
  }

  public async Task<IEnumerable<PostItemDto>> Search(Pager pager, string term, int author = 0, string include = "", bool sanitize = false)
  {
    term = term.ToLower();
    var skip = pager.CurrentPage * pager.ItemsPerPage - pager.ItemsPerPage;

    var results = new List<SearchResult>();
    var termList = term.ToLower().Split(' ').ToList();
    var categories = await _db.Categories.ToListAsync();

    foreach (var p in GetPosts(include, author))
    {
      var rank = 0;
      var hits = 0;

      foreach (var termItem in termList)
      {
        if (termItem.Length < 4 && rank > 0) continue;

        //var postCategories = categories.Where(c => c.)
        if (p.PostCategories != null && p.PostCategories.Count > 0)
        {
          foreach (var pc in p.PostCategories)
          {
            if (pc.Category.Content.ToLower() == termItem) rank += 10;
          }
        }
        if (p.Title.ToLower().Contains(termItem))
        {
          hits = Regex.Matches(p.Title.ToLower(), termItem).Count;
          rank += hits * 10;
        }
        if (p.Description.ToLower().Contains(termItem))
        {
          hits = Regex.Matches(p.Description.ToLower(), termItem).Count;
          rank += hits * 3;
        }
        if (p.Content.ToLower().Contains(termItem))
        {
          rank += Regex.Matches(p.Content.ToLower(), termItem).Count;
        }
      }
      if (rank > 0)
      {
        results.Add(new SearchResult { Rank = rank, Item = await PostToItem(p, sanitize) });
      }
    }

    results = results.OrderByDescending(r => r.Rank).ToList();

    var posts = new List<PostItemDto>();
    for (int i = 0; i < results.Count; i++)
    {
      posts.Add(results[i].Item);
    }
    pager.Configure(posts.Count);
    return await Task.Run(() => posts.Skip(skip).Take(pager.ItemsPerPage).ToList());
  }

  public async Task<Post> GetPostById(int id)
  {
    return await _db.Posts.Where(p => p.Id == id).FirstOrDefaultAsync();
  }

  public async Task<IEnumerable<PostItemDto>> GetPostItems()
  {
    var posts = await _db.Posts.ToListAsync();
    var postItems = new List<PostItemDto>();

    foreach (var post in posts)
    {
      postItems.Add(new PostItemDto
      {
        Id = post.Id,
        Title = post.Title,
        Description = post.Description,
        Content = post.Content,
        Slug = post.Slug,
        Author = _db.Authors.Where(a => a.Id == post.AuthorId).First(),
        Cover = string.IsNullOrEmpty(post.Cover) ? Constants.DefaultCover : post.Cover,
        Published = post.PublishedAt,
        PostViews = post.Views,
        Featured = post.IsFeatured
      });
    }

    return postItems;
  }

  public async Task<PostModel> GetPostModel(string slug)
  {
    var model = new PostModel();

    var all = _db.Posts
       .AsNoTracking()
       .Include(p => p.PostCategories)
       .OrderByDescending(p => p.IsFeatured)
       .ThenByDescending(p => p.PublishedAt).ToList();

    await SetOlderNewerPosts(slug, model, all);

    var post = _db.Posts.Single(p => p.Slug == slug);
    post.Views++;
    await _db.SaveChangesAsync();

    model.Related = await Search(new Pager(1), model.Post.Title, 0, "PF", true);
    model.Related = model.Related.Where(r => r.Id != model.Post.Id).ToList();

    return await Task.FromResult(model);
  }

  private async Task SetOlderNewerPosts(string slug, PostModel model, List<Post> all)
  {
    if (all != null && all.Count > 0)
    {
      for (int i = 0; i < all.Count; i++)
      {
        if (all[i].Slug == slug)
        {
          model.Post = await PostToItem(all[i]);

          if (i > 0 && all[i - 1].PublishedAt > DateTime.MinValue)
            model.Newer = await PostToItem(all[i - 1]);

          if (i + 1 < all.Count && all[i + 1].PublishedAt > DateTime.MinValue)
            model.Older = await PostToItem(all[i + 1]);

          break;
        }
      }
    }
  }

  public async Task<Post?> GetPostBySlug(string slug)
  {
    return await _db.Posts.Where(p => p.Slug == slug).FirstOrDefaultAsync();
  }

  public async Task<string> GetSlugFromTitle(string title)
  {
    string slug = title.ToSlug();
    var post = _db.Posts.Where(p => p.Slug == slug).FirstOrDefault();

    if (post != null)
    {
      for (int i = 2; i < 100; i++)
      {
        slug = $"{slug}{i}";
        if (_db.Posts.Where(p => p.Slug == slug).FirstOrDefault() == null)
        {
          return await Task.FromResult(slug);
        }
      }
    }
    return await Task.FromResult(slug);
  }

  public async Task<bool> Add(Post post)
  {
    var existing = await _db.Posts.Where(p => p.Slug == post.Slug).FirstOrDefaultAsync();
    if (existing != null)
      return false;

    post.CreatedAt = DateTime.UtcNow;

    // sanitize HTML fields
    post.Content = post.Content.RemoveScriptTags().RemoveImgTags();
    post.Description = post.Description.RemoveScriptTags().RemoveImgTags();

    await _db.Posts.AddAsync(post);
    return await _db.SaveChangesAsync() > 0;
  }

  public async Task<bool> Update(Post post)
  {
    var existing = await _db.Posts.Where(p => p.Slug == post.Slug).FirstOrDefaultAsync();
    if (existing == null)
      return false;

    existing.Slug = post.Slug;
    existing.Title = post.Title;
    existing.Description = post.Description.RemoveScriptTags().RemoveImgTags();
    existing.Content = post.Content.RemoveScriptTags().RemoveImgTags();
    existing.Cover = post.Cover;
    existing.PostType = post.PostType;
    existing.PublishedAt = post.PublishedAt;

    return await _db.SaveChangesAsync() > 0;
  }

  public async Task<bool> Publish(int id, bool publish)
  {
    var existing = await _db.Posts.Where(p => p.Id == id).FirstOrDefaultAsync();
    if (existing == null)
      return false;

    existing.PublishedAt = publish ? DateTime.UtcNow : DateTime.MinValue;

    return await _db.SaveChangesAsync() > 0;
  }

  public async Task<bool> Featured(int id, bool featured)
  {
    var existing = await _db.Posts.Where(p => p.Id == id).FirstOrDefaultAsync();
    if (existing == null)
      return false;

    existing.IsFeatured = featured;

    return await _db.SaveChangesAsync() > 0;
  }

  public async Task<IEnumerable<PostItemDto>> GetList(Pager pager, int author = 0, string category = "", string include = "", bool sanitize = true)
  {
    var skip = pager.CurrentPage * pager.ItemsPerPage - pager.ItemsPerPage;

    var posts = new List<Post>();
    foreach (var p in GetPosts(include, author))
    {
      if (string.IsNullOrEmpty(category))
      {
        posts.Add(p);
      }
      else
      {
        if (p.PostCategories != null && p.PostCategories.Count > 0)
        {
          Category cat = _db.Categories.Single(c => c.Content.ToLower() == category.ToLower());
          if (cat == null)
            continue;

          foreach (var pc in p.PostCategories)
          {
            if (pc.CategoryId == cat.Id)
            {
              posts.Add(p);
            }
          }
        }
      }
    }
    pager.Configure(posts.Count);

    var items = new List<PostItemDto>();
    foreach (var p in posts.Skip(skip).Take(pager.ItemsPerPage).ToList())
    {
      items.Add(await PostToItem(p, sanitize));
    }
    return await Task.FromResult(items);
  }

  public async Task<IEnumerable<PostItemDto>> GetPopular(Pager pager, int author = 0)
  {
    var skip = pager.CurrentPage * pager.ItemsPerPage - pager.ItemsPerPage;

    var posts = new List<Post>();

    if (author > 0)
      posts = _db.Posts.AsNoTracking().Where(p => p.PublishedAt > DateTime.MinValue && p.AuthorId == author)
           .OrderByDescending(p => p.Views).ThenByDescending(p => p.PublishedAt).ToList();
    else
      posts = _db.Posts.AsNoTracking().Where(p => p.PublishedAt > DateTime.MinValue)
           .OrderByDescending(p => p.Views).ThenByDescending(p => p.PublishedAt).ToList();

    pager.Configure(posts.Count);

    var items = new List<PostItemDto>();
    foreach (var p in posts.Skip(skip).Take(pager.ItemsPerPage).ToList())
    {
      items.Add(await PostToItem(p, true));
    }
    return await Task.FromResult(items);
  }

  public async Task<bool> Remove(int id)
  {
    var existing = await _db.Posts.Where(p => p.Id == id).FirstOrDefaultAsync();
    if (existing == null)
      return false;

    _db.Posts.Remove(existing);
    await _db.SaveChangesAsync();
    return true;
  }

  #region Private methods

  async Task<PostItemDto> PostToItem(Post p, bool sanitize = false)
  {
    var post = new PostItemDto
    {
      Id = p.Id,
      PostType = p.PostType,
      Slug = p.Slug,
      Title = p.Title,
      Description = p.Description,
      Content = p.Content,
      Categories = await _categoryProvider.GetPostCategories(p.Id),
      Cover = p.Cover,
      PostViews = p.Views,
      Rating = p.Rating,
      Published = p.PublishedAt,
      Featured = p.IsFeatured,
      Author = _db.Authors.Single(a => a.Id == p.AuthorId),
      SocialFields = new List<SocialField>()
    };

    if (post.Author != null)
    {
      if (string.IsNullOrEmpty(post.Author.Avatar))
        string.Format(Constants.AvatarDataImage, post.Author.DisplayName.Substring(0, 1).ToUpper());

      post.Author.Email = sanitize ? "donotreply@us.com" : post.Author.Email;
    }
    return await Task.FromResult(post);
  }

  List<Post> GetPosts(string include, int author)
  {
    var items = new List<Post>();
    var pubfeatured = new List<Post>();

    if (include.ToUpper().Contains(Constants.PostDraft) || string.IsNullOrEmpty(include))
    {
      var drafts = author > 0 ?
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt == DateTime.MinValue && p.AuthorId == author && p.PostType == PostType.Post).ToList() :
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt == DateTime.MinValue && p.PostType == PostType.Post).ToList();
      items = items.Concat(drafts).ToList();
    }

    if (include.ToUpper().Contains(Constants.PostFeatured) || string.IsNullOrEmpty(include))
    {
      var featured = author > 0 ?
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt > DateTime.MinValue && p.IsFeatured && p.AuthorId == author && p.PostType == PostType.Post).OrderByDescending(p => p.PublishedAt).ToList() :
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt > DateTime.MinValue && p.IsFeatured && p.PostType == PostType.Post).OrderByDescending(p => p.PublishedAt).ToList();
      pubfeatured = pubfeatured.Concat(featured).ToList();
    }

    if (include.ToUpper().Contains(Constants.PostPublished) || string.IsNullOrEmpty(include))
    {
      var published = author > 0 ?
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt > DateTime.MinValue && !p.IsFeatured && p.AuthorId == author && p.PostType == PostType.Post).OrderByDescending(p => p.PublishedAt).ToList() :
           _db.Posts.Include(p => p.PostCategories).Where(p => p.PublishedAt > DateTime.MinValue && !p.IsFeatured && p.PostType == PostType.Post).OrderByDescending(p => p.PublishedAt).ToList();
      pubfeatured = pubfeatured.Concat(published).ToList();
    }

    pubfeatured = pubfeatured.OrderByDescending(p => p.PublishedAt).ToList();
    items = items.Concat(pubfeatured).ToList();

    return items;
  }

  #endregion
}
