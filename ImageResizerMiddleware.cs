using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using System.Text;
using System.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace ImageResizer
{
    public class ImageResizerMiddleware
    {
        struct ResizeParams
        {
            public bool hasParams;
            public int w;
            public int h;
            public string format; // png, jpg, jpeg
            
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append($"w: {w}, ");
                sb.Append($"h: {h}, ");
                return sb.ToString();
            }
        }

        private readonly RequestDelegate _req;
        private readonly ILogger<ImageResizerMiddleware> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly IMemoryCache _memoryCache;

        private static readonly string[] suffixes = new string[] {
            ".png",
            ".jpg",
            ".jpeg"
        };

        public ImageResizerMiddleware(RequestDelegate req, IWebHostEnvironment env, ILogger<ImageResizerMiddleware> logger, IMemoryCache memoryCache)
        {
            _req = req;
            _env = env;
            _logger = logger;
            _memoryCache = memoryCache;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path;

            try
            {
                var rootPath = _env.WebRootPath ?? _env.ContentRootPath; //use web or content root path

                // hand to next middleware if we are not dealing with an image
                if (context.Request.Query.Count == 0 || !IsImagePath(path))
                {
                    await _req.Invoke(context);
                    return;
                }

                // hand to next middleware if we are dealing with an image but it doesn't have any usable resize querystring params
                var resizeParams = GetResizeParams(path, context.Request.Query);
                if (!resizeParams.hasParams)
                {
                    await _req.Invoke(context);
                    return;
                }

                // if we got this far, resize it
                _logger.LogInformation($"Resizing {path.Value} with params {resizeParams}");
                var provider = new PhysicalFileProvider(rootPath);
                var imagePath = provider.GetFileInfo(path.Value).PhysicalPath;
                
                // check file lastwrite
                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(imagePath);
                if (lastWriteTimeUtc.Year == 1601) // file doesn't exist, pass to next middleware
                {
                    await _req.Invoke(context);
                    return;
                }

                var imageData = GetImageData(imagePath, resizeParams, lastWriteTimeUtc);

                // write to stream
                context.Response.ContentType = resizeParams.format == "png" ? "image/png" : "image/jpeg";
                context.Response.ContentLength = imageData.Length;
                await context.Response.Body.WriteAsync(imageData, 0, (int)imageData.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resizing image on {0}", path);

                await _req.Invoke(context);
                return;
            }
        }

        private byte[] GetImageData(string imagePath, ResizeParams resizeParams, DateTime lastWriteTimeUtc)
        {
            // check cache and return if cached
            long cacheKey;
            unchecked
            {
                cacheKey = imagePath.GetHashCode() + lastWriteTimeUtc.ToBinary() + resizeParams.ToString().GetHashCode();
            }

            byte[] imageBytes;
            bool isCached = _memoryCache.TryGetValue<byte[]>(cacheKey, out imageBytes);
            if (isCached)
            {
                _logger.LogInformation("Serving from cache");
                return imageBytes;
            }

            using var image = Image.Load(imagePath);

            // if either w or h is 0, set it based on ratio of original image
            if (resizeParams.h == 0)
                resizeParams.h = (int)Math.Round(image.Height * (float)resizeParams.w / image.Width);

            if (resizeParams.w == 0)
                resizeParams.w = (int)Math.Round(image.Width * (float)resizeParams.h / image.Height);

            // resize
            using var resized = image.Clone(_ => _.Resize(resizeParams.w, resizeParams.h));

            // encode
            using var imageStream = new MemoryStream();
            resized.Save(imageStream, resizeParams.format == "png" ? new PngEncoder() : new JpegEncoder());

            var imageData = imageStream.ToArray();

            // cache the result
            _memoryCache.Set<byte[]>(cacheKey, imageData);

            return imageData;
        }

        private bool IsImagePath(PathString path)
        {
            if (path == null || !path.HasValue)
                return false;

            return suffixes.Any(x => x.EndsWith(x, StringComparison.OrdinalIgnoreCase));
        }

        private ResizeParams GetResizeParams(PathString path, IQueryCollection query)
        {
            ResizeParams resizeParams = new ResizeParams();

            // before we extract, do a quick check for resize params
            resizeParams.hasParams =
                resizeParams.GetType().GetTypeInfo()
                .GetFields().Where(f => f.Name != "hasParams")
                .Any(f => query.ContainsKey(f.Name));

            // if no params present, bug out
            if (!resizeParams.hasParams)
                return resizeParams;

            // extract resize params

            if (query.ContainsKey("format"))
                resizeParams.format = query["format"];
            else
                resizeParams.format = path.Value.Substring(path.Value.LastIndexOf('.') + 1);

            int w = 0;
            if (query.ContainsKey("w"))
                int.TryParse(query["w"], out w);
            resizeParams.w = w;

            int h = 0;
            if (query.ContainsKey("h"))
                int.TryParse(query["h"], out h);
            resizeParams.h = h;

            return resizeParams;
        }
    }
}
