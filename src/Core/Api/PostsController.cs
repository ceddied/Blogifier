﻿using Core.Data;
using Core.Helpers;
using Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Api
{
    [Route("api/[controller]")]
    [ApiController]
    public class PostsController : ControllerBase
    {
        IDataService _data;

        public PostsController(IDataService data)
        {
            _data = data;
        }

        /// <summary>
        /// Get list of blog posts
        /// </summary>
        /// <param name="term">Search term</param>
        /// <param name="status">Status; P - published, D - drafts</param>
        /// <param name="page">Page number</param>
        /// <returns>Model with list of posts and pager</returns>
        [HttpGet]
        public async Task<ActionResult<PageListModel>> Get([FromQuery]string term = "", [FromQuery]string status = "", [FromQuery]int page = 1)
        {
            try
            {
                var blog = await _data.CustomFields.GetBlogSettings();
                var pager = new Pager(page, blog.ItemsPerPage);
                var author = _data.Authors.Single(a => a.AppUserName == User.Identity.Name);
                IEnumerable<PostItem> results;

                if(!string.IsNullOrEmpty(term))
                {
                    results = author.IsAdmin ? 
                        await _data.BlogPosts.Search(pager, term) :
                        await _data.BlogPosts.Search(pager, term, author.Id);
                }
                else
                {
                    if(!author.IsAdmin)
                    {
                        if(status == "P")
                            results = await _data.BlogPosts.GetList(p => p.Published > DateTime.MinValue && p.AuthorId == author.Id, pager);
                        else if(status == "D")
                            results = await _data.BlogPosts.GetList(p => p.Published == DateTime.MinValue && p.AuthorId == author.Id, pager);
                        else
                            results = await _data.BlogPosts.GetList(p => p.AuthorId == author.Id, pager);  
                    }
                    else
                    {
                        if(status == "P")
                            results = await _data.BlogPosts.GetList(p => p.Published > DateTime.MinValue, pager);
                        else if(status == "D")
                            results = await _data.BlogPosts.GetList(p => p.Published == DateTime.MinValue, pager);
                        else
                            results = await _data.BlogPosts.GetList(p => p.Id > 0, pager);
                    }                
                }
                return Ok(new PageListModel { Posts = results, Pager = pager });
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Database Failure");
            }
        }

        /// <summary>
        /// Get single post by ID
        /// </summary>
        /// <param name="id">Post ID</param>
        /// <returns>Post item</returns>
        [HttpGet("{id}")]
        public async Task<PostItem> GetPost(int id)
        {
            if (id > 0)
            {
                return await _data.BlogPosts.GetItem(p => p.Id == id);
            }
            else
            {
                var author = await _data.Authors.GetItem(a => a.AppUserName == User.Identity.Name);
                var blog = await _data.CustomFields.GetBlogSettings();
                return new PostItem { Author = author, Cover = blog.Cover };
            }               
        }

        /// <summary>
        /// Set post as published or draft
        /// </summary>
        /// <param name="id">Post ID</param>
        /// <param name="flag">Flag; P - publish, U - unpublish</param>
        /// <returns>Success of failure</returns>
        [HttpPut("publish")]
        [Authorize]
        public async Task<ActionResult> Publish(int id, string flag)
        {
            try
            {
                var post = _data.BlogPosts.Single(p => p.Id == id);
                var author = _data.Authors.Single(a => a.Id == post.AuthorId);
                var user = _data.Authors.Single(a => a.AppUserName == User.Identity.Name);
                if (!string.IsNullOrEmpty(flag) && (user.IsAdmin || author.AppUserName == User.Identity.Name))
                {
                    if (flag == "P") post.Published = DateTime.UtcNow;
                    if (flag == "U") post.Published = DateTime.MinValue;
                    _data.Complete();
                }
                await Task.CompletedTask;

                return Ok(Resources.Updated);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        /// <summary>
        /// Set post as featured
        /// </summary>
        /// <param name="id">Post ID</param>
        /// <param name="flag">Flag; F - featured, U - remove from featured</param>
        /// <returns></returns>
        [HttpPut("feature")]
        [Administrator]
        public async Task<ActionResult> Feature(int id, string flag)
        {
            try
            {
                var post = _data.BlogPosts.Single(p => p.Id == id);
                var author = _data.Authors.Single(a => a.Id == post.AuthorId);
                var user = _data.Authors.Single(a => a.AppUserName == User.Identity.Name);
                if (!string.IsNullOrEmpty(flag) && (user.IsAdmin || author.AppUserName == User.Identity.Name))
                {
                    if (flag == "F") post.IsFeatured = true;
                    if (flag == "U") post.IsFeatured = false;
                    _data.Complete();
                }
                await Task.CompletedTask;

                return Ok("Updated");
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        /// <summary>
        /// Save blog post
        /// </summary>
        /// <param name="post">Post item</param>
        /// <returns>Saved post item</returns>
        [HttpPost]
        [Authorize]
        public async Task<ActionResult<PostItem>> Post(PostItem post)
        {
            try
            {
                post.Slug = await GetSlug(post.Id, post.Title);
                var saved = await _data.BlogPosts.SaveItem(post);
                return Created($"admin/posts/edit?id={saved.Id}", saved);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        /// <summary>
        /// Remove post item
        /// </summary>
        /// <param name="id">Post ID</param>
        /// <returns>Success or failure</returns>
        [HttpDelete("remove/{id}")]
        [Authorize]
        public async Task<IActionResult> Remove(int id)
        {           
            try
            {
                var post = _data.BlogPosts.Single(p => p.Id == id);
                var author = _data.Authors.Single(a => a.Id == post.AuthorId);
                var user = _data.Authors.Single(a => a.AppUserName == User.Identity.Name);

                if (user.IsAdmin || author.AppUserName == User.Identity.Name)
                {
                    _data.BlogPosts.Remove(post);
                    _data.Complete();
                }    
                await Task.CompletedTask;

                return Ok(Resources.Removed);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        async Task<string> GetSlug(int id, string title)
        {
            string slug = title.ToSlug();
            BlogPost post;

            if (id == 0)
                post = _data.BlogPosts.Single(p => p.Slug == slug);
            else
                post = _data.BlogPosts.Single(p => p.Slug == slug && p.Id != id);

            if (post == null)
                return await Task.FromResult(slug);

            for (int i = 2; i < 100; i++)
            {
                if (id == 0)
                    post = _data.BlogPosts.Single(p => p.Slug == $"{slug}{i}");
                else
                    post = _data.BlogPosts.Single(p => p.Slug == $"{slug}{i}" && p.Id != id);

                if (post == null)
                {
                    return await Task.FromResult(slug + i.ToString());
                }
            }

            return await Task.FromResult(slug);
        }
    }
}