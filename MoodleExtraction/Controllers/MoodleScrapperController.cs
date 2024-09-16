using Microsoft.AspNetCore.Mvc;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeleniumExtras.WaitHelpers;
using System;
using System.Net;
using File = System.IO.File;
using Azure.Storage.Blobs;
using MoodleExtraction.Models;
using Azure.Storage.Blobs.Models;
using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Azure.Cosmos;
using HtmlAgilityPack;
using static System.Collections.Specialized.BitVector32;
using Microsoft.Azure.Cosmos.Scripts;
using System.Reflection.Emit;

namespace MoodleExtraction.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MoodleScraperController : ControllerBase
    {
        private readonly CosmosClient _cosmosClient;
        private readonly Microsoft.Azure.Cosmos.Container _cosmosContainer;
        private const string SasToken = "?sp=r&st=2024-09-12T10:39:10Z&se=2025-08-01T21:39:10Z&sv=2022-11-02&sr=c&sig=rZjL2NQ1P7LwCN98QWlNoSJYS420ys0cAts9a25KXJU%3D";

        public MoodleScraperController()
        {

            _cosmosClient = new CosmosClient("AccountEndpoint=https://cosmos-ppn-db.documents.azure.com:443/;AccountKey=nrIPeoQwxBvOTu1Nmmc9hOjp2QAWZQayygN3gdwKfDCDjkG4LeNYbcIeSrBZtot0gpFo3KelB9jRACDbh9czcA==;");
            _cosmosContainer = _cosmosClient.GetContainer("ppn-db", "Courses");
        }
        [HttpGet("scrape-category")]
        public async Task<IActionResult> ScrapeCategory(int categoryId)
        {
//            await ProcessCourseAfterDownload("La tension électrique", "C:\\Users\\HichameAFIFI\\Downloads\\backup scrap\\Le courant électrique continu - Notion de la tension électrique");

            try
            {
                var options = new ChromeOptions();
                //options.AddArgument("--headless"); // Run in headless mode (no GUI)

                using (var driver = new ChromeDriver(options))
                {
                    // Step 1: Login
                    driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
                    driver.FindElement(By.Id("username")).SendKeys("alexsys");
                    driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
                    driver.FindElement(By.Id("loginbtn")).Click();
                    await Task.Delay(2000); // Wait for login to complete

                    // Step 2: Navigate to the category page
                    string categoryUrl = $"https://m3.inpt.ac.ma/course/index.php?categoryid={categoryId}";
                    driver.Navigate().GoToUrl(categoryUrl);

                    // Step 3: Extract course names, URLs, and images
                    var courseElements = driver.FindElements(By.CssSelector("div.theme-course-item-inner"));
                    var courses = new List<dynamic>();
                    string Filiere = "";
                    string Matiere = "";
                    foreach (var courseElement in courseElements)
                    {
                        try
                        {
                            var nameElement = courseElement.FindElement(By.CssSelector("h4.title a"));
                            var imageElement = courseElement.FindElement(By.CssSelector("div.image-wrap div.image img"));

                            string courseName = nameElement.Text;
                            string courseUrl = nameElement.GetAttribute("href");
                            string imageUrl = imageElement.GetAttribute("src");



                            var breadcrumbItems = driver.FindElements(By.CssSelector("ol.breadcrumb li.breadcrumb-item a"));

                            // Extract the relevant breadcrumb values
                            Filiere = breadcrumbItems[2].Text; // "1ère APIC"
                            Matiere = breadcrumbItems[3].Text; // "Mathématiques"

                            // Now use the breadcrumb values dynamically in the course directory path
                            string sanitizedCategory1 = SanitizeFileName(Filiere);
                            string sanitizedCategory2 = SanitizeFileName(Matiere);
                            // Update the call to GenerateCourseJson to pass the list of sections

                            string sanitizedCourseName = SanitizeFileName(courseName);

                            string courseDirectory = Path.Combine(sanitizedCategory1, sanitizedCategory2, sanitizedCourseName);

                            // **Check if the course directory already exists and contains subfolders**
                            if (Directory.Exists(courseDirectory))
                            {
                                Console.WriteLine($"Course '{courseName}' already downloaded. Skipping...");
                                // **Immediately process this course for JSON and Blob Upload**
                                /*                                await ProcessCourseAfterDownload(courseName, courseDirectory);
                                */
                                continue; // Skip this course and move to the next one

                            }

                            Directory.CreateDirectory(courseDirectory);


                            // **Download the course image immediately after creating the folder**
                            var imageName = await DownloadImage(driver, imageUrl, courseDirectory);

                            courses.Add(new { Name = courseName, Url = courseUrl, Image = imageName });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing course: {ex.Message}");
                            continue;

                        }
                    }

                    foreach (var course in courses)
                    {
                        try
                        {
                            // Step 4: Navigate to the course page and extract relevant content
                            string sanitizedCategory1 = SanitizeFileName(Filiere);
                            string sanitizedCategory2 = SanitizeFileName(Matiere);
                            string courseDirectory = Path.Combine(sanitizedCategory1, sanitizedCategory2, SanitizeFileName(course.Name));

                            List<string> sectionDirectories = Directory.GetDirectories(courseDirectory).ToList();

                            await ScrapeCourse(driver, course.Url, Path.Combine(sanitizedCategory1, sanitizedCategory2, SanitizeFileName(course.Name)));
                            // **After scraping, immediately process the course**
                            await ProcessCourseAfterDownload(course.Name, courseDirectory);

                        }
                        catch (StaleElementReferenceException ex)
                        {
                            Console.WriteLine($"Stale element reference for course: {ex.Message}");
                            continue;

                        }
                    }
                }


                return Ok("Scraping completed successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }
        [HttpPost("process-courses")]
        public async Task<IActionResult> ProcessCourses([FromBody] ProcessCoursesRequest request)
        {
            try
            {
                // Validate the paths
                if (!Directory.Exists(request.CoursesPath))
                {
                    return BadRequest("The specified courses path does not exist.");
                }

                /** if (!Directory.Exists(request.H5PPath))
                 {
                     return BadRequest("The specified H5P path does not exist.");
                 }**/

                // Process each course
                var courseDirectories = Directory.GetDirectories(request.CoursesPath);

                foreach (var courseDirectory in courseDirectories)
                {
                    string courseName = Path.GetFileName(courseDirectory);
                    var sectionDirectories = Directory.GetDirectories(courseDirectory).ToList();

                    if (sectionDirectories.Any())
                    {
                        // Generate JSON for the course
                        List<ElementProgramme> elementProgrammes = new List<ElementProgramme>
                {
                    new ElementProgramme { Level = 1, Code = "2d1d190d-5150-42f3-879d-0097063c806e" },
                    new ElementProgramme { Level = 2, Code = "395c7c8e-475a-45e9-a0b4-11ef241a6463" },
                    new ElementProgramme { Level = 3, Code = "d4892214-c5a8-4294-9eda-c3c552d49b79" }
                };

                        await GenerateCourseJson(courseName, courseDirectory, null, sectionDirectories, elementProgrammes);
                    }
                }

                // Upload all contents to Blob Storage
                await UploadToBlobStorageAsync(request.CoursesPath);

                return Ok("Courses processed and uploaded successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        private async Task ProcessCourseAfterDownload(string courseName, string courseDirectory)
        {
            // Get the list of section directories within the course directory
            var sectionDirectories = Directory.GetDirectories(courseDirectory).ToList();

            if (sectionDirectories.Any())
            {
                // Generate JSON for the course
                List<ElementProgramme> elementProgrammes = new List<ElementProgramme>
        {
            new ElementProgramme { Level = 1, Code = "2d1d190d-5150-42f3-879d-0097063c806e" },
            new ElementProgramme { Level = 2, Code = "395c7c8e-475a-45e9-a0b4-11ef241a6463" },
            new ElementProgramme { Level = 3, Code = "d4892214-c5a8-4294-9eda-c3c552d49b79" }
        };

                await GenerateCourseJson(courseName, courseDirectory, null, sectionDirectories, elementProgrammes);
            }

            // Upload all contents to Blob Storage
            await UploadToBlobStorageAsync(courseDirectory);
        }
        private async Task ScrapeCourse(IWebDriver driver, string courseUrl, string courseDirectory)
        {
            driver.Navigate().GoToUrl(courseUrl);
            await Task.Delay(2000); // Wait for the page to load

            // Step 4a: Extract sections and images within the content
            var sections = driver.FindElements(By.CssSelector("div[role='main'] ul.tiles li.tile.tile-clickable.phototile.altstyle"));
            var orderedSections = sections
         .OrderBy(x => int.Parse(x.GetAttribute("data-section")))
         .ToList();

            int sectionId = 1;
            foreach (var section in orderedSections)
            {
                try
                {
                    string tileId = $"tile-{sectionId}";
                    string sectionById = $"section-{sectionId}";

                    var tileLi = driver.FindElement(By.Id(tileId));
                    var photoTileTextDiv = tileLi.FindElement(By.CssSelector("div.photo-tile-text"));

                    var sectionNameElement = photoTileTextDiv.FindElement(By.TagName("h3"));
                    string sectionName = SanitizeFileName(sectionNameElement.Text);

                    string sectionDirectory = Path.Combine(courseDirectory, sectionName + "-" + sectionId.ToString());
                    Directory.CreateDirectory(sectionDirectory);

                    var activityLinkElement = tileLi.FindElement(By.CssSelector("a.tile-link"));
                    string sectionActivityUrl = activityLinkElement.GetAttribute("href");

                    
                    // Scrape activity and images in course content
                    await ScrapeActivity(driver, sectionActivityUrl, sectionById, sectionDirectory, sectionName);

                    // Navigate back to the course URL
                    driver.Navigate().GoToUrl(courseUrl);
                    await Task.Delay(2000);


                    sectionId++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Element not found for section {sectionId}: {ex.Message}");
                    continue;
                }




                //foreach (var section in orderedSections)
                //{
                //    try
                //    {
                //        string tileId = $"tile-{sectionId}";
                //        string sectionById = $"section-{sectionId}";

                //        var tileLi = driver.FindElement(By.Id(tileId));
                //        var photoTileTextDiv = tileLi.FindElement(By.CssSelector("div.photo-tile-text"));

                //        var sectionNameElement = photoTileTextDiv.FindElement(By.TagName("h3"));
                //        string sectionName = SanitizeFileName(sectionNameElement.Text);

                //        string tabIndex = section.GetAttribute("tabindex");

                //        string dataSection = section.GetAttribute("data-section");

                //        string sectionDirectory = Path.Combine(courseDirectory, sectionName+"-"+ dataSection);

                //        Directory.CreateDirectory(sectionDirectory);

                //        var activityLinkElement = tileLi.FindElement(By.CssSelector("a.tile-link"));
                //        string sectionActivityUrl = activityLinkElement.GetAttribute("href");

                //        // Scrape activity and images in course content
                //        await ScrapeActivity(driver, sectionActivityUrl, sectionById, sectionDirectory, sectionName);

                //        driver.Navigate().GoToUrl(courseUrl);
                //        await Task.Delay(2000);

                //        sectionId++;
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine($"Element not found for section {sectionId}: {ex.Message}");
                //        continue;
                //    }
            }
        }

        private async Task ScrapeActivity(IWebDriver driver, string activityUrl, string sectionId, string sectionDirectory, string sectionName)
        {
            driver.Navigate().GoToUrl(activityUrl);

            // Wait for the section to be visible
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
            wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.format_tiles_section_content")));

            await Task.Delay(2000); // Wait for any additional loading

            // Try to locate elements, handling stale element reference exceptions
            bool elementsFound = false;
            int attempts = 0;

            // Retry finding elements up to 3 times in case of a stale element
            List<string> sectionElements = new List<string>();

            while (!elementsFound && attempts < 3)
            {
                try
                {
                    // Locate the section element by ID
                    var section = driver.FindElement(By.Id(sectionId));
                    var sec = section.FindElement(By.CssSelector("div.format_tiles_section_content"));

                    // Find the section title and check if it contains "Média"
                    var sectionTitleElement = sec.FindElement(By.CssSelector("div.sectiontitle h2"));
                    string sectionTitle = sectionTitleElement.Text.Trim();

                    if (sectionTitle.Contains("Média"))
                    {
                        // Handle media content based on the section title
                        await DownloadMediaContent(driver, section, sectionDirectory);
                        return; // Exit the method after handling media content
                    }

                    // Find the first <ul> element within the located div
                    var ulElement = sec.FindElement(By.CssSelector("ul.section.img-text.nosubtiles"));

                    // Find all <li> elements and store their IDs and references in a list
                    sectionElements = ulElement.FindElements(By.CssSelector("li.activity"))
                        ?.Select(x => x.GetAttribute("id"))
                        .ToList();

                    elementsFound = true;
                }
                catch (StaleElementReferenceException)
                {
                    attempts++;
                    await Task.Delay(1000); // Wait and retry
                }
            }

            if (sectionElements.Count == 0)
            {
                await DownloadPageContent(driver, "", sectionDirectory, sectionName);
            }
            else
            {
                await DownloadDescriptionContent(driver, sectionDirectory, "Description");
            }

            int i = 0;



            Console.WriteLine($"Total section elements found: {sectionElements?.Count}");

            foreach (var sectionElementId in sectionElements)
            {
                IWebElement section = null ;
                section = driver.FindElement(By.Id(sectionElementId));

                try
                {
                    // Check if the section element is null or has an empty ID
                    if (sectionElementId == null || string.IsNullOrEmpty(sectionElementId))
                    {
                        Console.WriteLine("Element is null or has an empty ID.");
                        continue; // Skip this iteration if the ID is invalid
                    }

                    // Re-locate the section element each time to ensure a fresh reference

                    // Perform operations with the fresh element
                    var typeElement = section?.FindElement(By.CssSelector("div.text-uppercase.small"));
                    var nameElement = section?.FindElement(By.CssSelector("div.activityname a span.instancename"));
                    string type = typeElement.Text.Trim();
                    var nameText = nameElement.Text;

                    //if (type != "TEST") continue;

                    // Handle hidden span elements
                    var hiddenSpanElements = nameElement.FindElements(By.CssSelector("span.accesshide"));
                    if (hiddenSpanElements.Count > 0)
                    {
                        var hiddenText = hiddenSpanElements[0].Text;
                        nameText = nameText.Replace(hiddenText, "").Trim();
                    }

                    string activityName = SanitizeFileName(nameText);
                    var element = section?.FindElement(By.CssSelector("div.activityname a"));
                    string sectionActivityUrl = element.GetAttribute("href");

                    // Download content based on type
                    switch (type)
                    {
                        case "TEST":
                            await DownloadTestContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                            break;
                        case "H5P":
                            await DownloadH5PContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                            break;
                        case "GEOGEBRA":
                            await DownloadGeoGebraContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                            break;
                        case "FICHIER":
                            var activityContainerElement = section?.FindElement(By.CssSelector("div.tiles-activity-container"));
                            string dataUrl = activityContainerElement.GetAttribute("data-url");

                            if (!string.IsNullOrEmpty(dataUrl))
                            {
                                await DownloadPdfFile(driver, dataUrl, sectionDirectory);
                            }
                            break;
                        case "PAGE":
                            await DownloadPageContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                            driver.Navigate().GoToUrl(activityUrl); // Corrected from activityUrl to sectionActivityUrl
                                                                           // Wait for the section to be visible
                            WebDriverWait wait_ = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                            wait_.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.format_tiles_section_content")));
                            break;


                    }
                    
                    if (type == "TEST") continue;
                    if (type == "H5P") continue;
                    if (type == "GEOGEBRA") continue;
                    if (type == "FICHIER") continue;

                }
                catch (NoSuchElementException ex)
                {
                    Console.WriteLine($"NoSuchElementException: {ex.Message}");
                }
                catch (StaleElementReferenceException ex)
                {
                    Console.WriteLine($"StaleElementReferenceException: {ex.Message}");
                    // You might want to refresh the sectionElement reference here if needed
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error: {ex.Message}");
                }



                // Handle attributes and different dataModTypes
                string dataModType = string.Empty;
                string dataTitle = string.Empty;
                string dataId = string.Empty;

                try
                {
                    //continue;

                    // Re-locate element before accessing attributes
                    dataModType = section?.GetAttribute("data-modtype");
                    dataTitle = section?.GetAttribute("data-title");
                    dataId = section?.GetAttribute("data-id");

                    // Handle various dataModTypes
                    if (dataModType == "feedback") continue;

                    if (dataModType == "scorm")
                    {
                        //await DownloadScormContent(driver, sectionDirectory, sectionDirectory, dataTitle);
                        continue;
                    }

                    if (dataModType == "label")
                    {
                        i++;
                        await DownloadLabelContent(driver, sectionDirectory, dataTitle, i, dataId);
                        continue;
                    }
                    else
                    {
                        i = 0;
                    }
                }
                catch (NoSuchElementException ex)
                {
                    Console.WriteLine($"NoSuchElementException: {ex.Message}");
                }
                catch (StaleElementReferenceException ex)
                {
                    Console.WriteLine($"StaleElementReferenceException: {ex.Message}");
                    // You might want to refresh the sectionElement reference here if needed
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error: {ex.Message}");
                }
            }


        }
        private async Task DownloadMediaContent(IWebDriver driver, IWebElement section, string sectionDirectory)
        {
            try
            {
                // Locate the video tag within the section
                var videoElement = section.FindElement(By.CssSelector("video"));

                if (videoElement != null)
                {
                    // Locate the source tag within the video element and get the src attribute
                    var sourceElement = videoElement.FindElement(By.TagName("source"));
                    string videoUrl = sourceElement.GetAttribute("src");

                    if (!string.IsNullOrEmpty(videoUrl))
                    {
                        // Open the video URL in a new tab and download it
                        await DownloadVideoFile(driver, videoUrl, sectionDirectory);
                    }
                }
            }
            catch (NoSuchElementException)
            {
                // Skip to the next element if video tag or source is not found
                Console.WriteLine($"Video element or source not found in section.");
            }
        }

        private async Task DownloadVideoFile(IWebDriver driver, string videoUrl, string directory)
        {
            try
            {
                // Open the video URL in a new tab and switch to it
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());
                driver.Navigate().GoToUrl(videoUrl);

                await Task.Delay(2000); // Wait for the video to load

                Uri uri = new Uri(videoUrl);
                string fileName = Path.GetFileName(uri.LocalPath);

                // If the file has no extension, assume it's a video and add ".mp4"
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName += ".mp4";
                }

                string filePath = Path.Combine(directory, fileName);

                // Use HttpClient with cookie handling to download the video
                using (var handler = new HttpClientHandler())
                {
                    var cookies = driver.Manage().Cookies.AllCookies;
                    handler.CookieContainer = new CookieContainer();

                    foreach (var cookie in cookies)
                    {
                        handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                    }

                    using (HttpClient client = new HttpClient(handler))
                    {
                        var videoBytes = await client.GetByteArrayAsync(videoUrl);
                        await System.IO.File.WriteAllBytesAsync(filePath, videoBytes);
                        Console.WriteLine($"Video downloaded to: {filePath}");
                    }
                }

                // Close the new tab and switch back to the original tab
                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download video: {videoUrl}. Error: {ex.Message}");
            }
        }

        private async Task<string> DownloadImage(IWebDriver driver, string imageUrl, string directory)
        {
            try
            {

                // Open image in a new tab and download
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());

                driver.Navigate().GoToUrl(imageUrl);
                await Task.Delay(2000); // Wait for the image to load

                Uri uri = new Uri(imageUrl);
                string fileName = Path.GetFileName(uri.LocalPath);

                // Check if the file has no extension and is likely an image
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName += ".jpg"; // Add .jpg extension if missing
                }

                string filePath = Path.Combine(directory, fileName);

                using (var handler = new HttpClientHandler())
                {
                    var cookies = driver.Manage().Cookies.AllCookies;
                    handler.CookieContainer = new CookieContainer();

                    foreach (var cookie in cookies)
                    {
                        handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                    }

                    using (HttpClient client = new HttpClient(handler))
                    {
                        var imageBytes = await client.GetByteArrayAsync(imageUrl);
                        await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);
                    }
                }

                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());

                Console.WriteLine($"Image downloaded to: {filePath}");
                string blobConnectionString = "DefaultEndpointsProtocol=https;AccountName=stppncours;AccountKey=mqsGyKxuR2tnJ5RpqRjR/wTgKlrVCFo4mV2H3vAlTuvmoYY5jRMxQ/UkQqCGLO5yOXwTXLYEcm+p+AStwCbyjQ==;EndpointSuffix=core.windows.net";
                string coursesContainerName = "courses";
                BlobContainerClient containerClient = new BlobContainerClient(blobConnectionString, coursesContainerName);

                // Upload the downloaded image to Blob Storage
                string blobName = "/" + Path.Combine(directory, fileName).Replace("\\", "/"); // Ensure the directory structure is preserved in blob storagesu
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                // Set MIME type for the image (optional, assumes image/jpg)
                var blobHttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "image/jpeg" // You can use a function to detect the actual MIME type
                };

                using FileStream fileStream = System.IO.File.OpenRead(filePath);
                await blobClient.UploadAsync(fileStream, new BlobUploadOptions
                {
                    HttpHeaders = blobHttpHeaders
                });

                // Return the image file name
                return fileName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download image: {imageUrl}. Error: {ex.Message}");
                return null;
            }
        }

        private async Task DownloadPageContent(IWebDriver driver, string pageUrl, string sectionDirectory, string activityName)
        {
            try
            {
                if (pageUrl != "")
                {
                    // Open a new tab and switch to it
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());

                    // Navigate to the PAGE URL in the new tab

                    driver.Navigate().GoToUrl(pageUrl);
                    await Task.Delay(2000); // Wait for the page to load
                }
                // Step 1: Extract JS and CSS files
                string pageHtml = driver.PageSource;

                // Remove unwanted HTML elements
                pageHtml = RemoveUnwantedHtmlElements(pageHtml);
                string scriptsDirectory = Path.Combine(sectionDirectory, "scripts");
                string stylesDirectory = Path.Combine(sectionDirectory, "styles");
                string imagesDirectory = Path.Combine(sectionDirectory, "images");

                Directory.CreateDirectory(scriptsDirectory);
                Directory.CreateDirectory(stylesDirectory);
                Directory.CreateDirectory(imagesDirectory);

                string wwwRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");


                string bootstrapCssPath = Path.Combine(wwwRootPath, "css", "bootstrap.min.css");
                string destinationCssPath = Path.Combine(stylesDirectory, "bootstrap.min.css");
                System.IO.File.Copy(bootstrapCssPath, destinationCssPath, true); // Copy and overwrite if exists


                string[] jsFiles = { "bootstrap.bundle.min.js" };
                foreach (var jsFile in jsFiles)
                {
                    string sourceJsPath = Path.Combine(wwwRootPath, "js", jsFile);
                    string destinationJsPath = Path.Combine(scriptsDirectory, jsFile);
                    System.IO.File.Copy(sourceJsPath, destinationJsPath, true); // Copy and overwrite if exists
                }




                // Step 2: Extract HTML content from the main div
                string mainContentHtml = "";
                string head = "";
                if (pageUrl == "")
                {
                    try
                    {
                        var mainDiv = driver.FindElement(By.CssSelector("div.format_tiles_section_content .summary"));
                        mainContentHtml = mainDiv.GetAttribute("outerHTML");


                        int headStartIndex = pageHtml.IndexOf("<head");  // Find the start of the <head> tag
                        int headEndTagIndex = pageHtml.IndexOf(">", headStartIndex);  // Find the end of the opening <head> tag
                        int headCloseTagIndex = pageHtml.IndexOf("</head>");  // Find the closing </head> tag

                        if (headStartIndex != -1 && headEndTagIndex != -1 && headCloseTagIndex != -1)
                        {
                            // Extract the content between the <head> and </head> tags
                            head = pageHtml.Substring(headStartIndex, (headCloseTagIndex + 7) - headStartIndex);
                        }
                        else
                        {
                            Console.WriteLine("Head tag not found in the HTML document.");
                        }



                    }
                    catch (Exception ex)
                    {

                        throw ex;
                    }


                }
                else
                {
                    var mainDiv = driver.FindElement(By.CssSelector("div[role='main']"));
                    mainContentHtml = mainDiv.GetAttribute("outerHTML");
                }

                string bootstrapCssLink = $"<link rel=\"stylesheet\" href=\"{Path.Combine("styles", "bootstrap.min.css") + SasToken}\">";
                head += bootstrapCssLink;


                // Add meta charset tag and combine the modified HTML content with the main content
                pageHtml = "<html><head><meta charset=\"UTF-8\">" + head + "</head><body>" + mainContentHtml + "</body></html>";

                pageHtml = Regex.Replace(pageHtml, @"<div[^>]*class=['""][^'""]*theme-coursenav[^'""]*['""][^>]*>.*?</div>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                string bootstrapJsLinks = @$"<script src='{Path.Combine("scripts", "bootstrap.bundle.min.js") + SasToken}'></script>";

                pageHtml += bootstrapJsLinks + "</body></html>";



                pageHtml = pageHtml.Replace("data-toggle", "data-bs-toggle")
                       .Replace("data-target", "data-bs-target")
                       .Replace("data-dismiss", "data-bs-dismiss");

                // Use Regex to find all content inside the modal-dialog elements
                Regex modalDialogRegex = new Regex(@"<div\s+class=['""]modal-dialog['""].*?>.*?</div>\s*</div>", RegexOptions.Singleline);
                MatchCollection modalDialogMatches = modalDialogRegex.Matches(pageHtml);

                // Iterate through each match and replace href attributes within the matched content
                foreach (Match match in modalDialogMatches)
                {
                    string modalContent = match.Value;

                    // Replace all hrefs with '#' inside the modal-dialog content
                    string updatedModalContent = Regex.Replace(modalContent, @"href=""[^""]*""", "href=\"#\"");

                    // Replace the old modal-dialog content with the updated content in the main HTML
                    pageHtml = pageHtml.Replace(modalContent, updatedModalContent);
                }





                pageHtml = await DownloadAndReplaceResources(driver, pageHtml, scriptsDirectory, stylesDirectory, imagesDirectory);

                // Step 3: Download media files
                //pageHtml = await DownloadImagesAndUpdatePaths(driver, pageHtml, imagesDirectory);
                pageHtml = await DownloadVideosAndUpdatePaths(driver, pageHtml, imagesDirectory);

                // Save the final HTML content to a file
                string pageFilePath = Path.Combine(sectionDirectory, $"{activityName}.html");
                await System.IO.File.WriteAllTextAsync(pageFilePath, pageHtml);

                // Close the new tab and switch back to the original tab
                if (pageUrl != "")
                {
                    driver.Close();

                    driver.SwitchTo().Window(driver.WindowHandles.First());
                }


            }
            catch (Exception ex)
            {

                throw ex;
            }

        }

        private async Task DownloadLabelContent(IWebDriver driver, string sectionDirectory, string activityName, int order, string id)
        {
            try
            {
                // Step 1: Extract JS and CSS files
                string pageHtml = driver.PageSource;

                // Remove unwanted HTML elements
                pageHtml = RemoveUnwantedHtmlElements(pageHtml);
                string stylesDirectory = Path.Combine(sectionDirectory, "styles");
                string imagesDirectory = Path.Combine(sectionDirectory, "images");

                Directory.CreateDirectory(stylesDirectory);
                Directory.CreateDirectory(imagesDirectory);


                string wwwRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");


                string bootstrapCssPath = Path.Combine(wwwRootPath, "css", "bootstrap.min.css");
                string destinationCssPath = Path.Combine(stylesDirectory, "bootstrap.min.css");
                System.IO.File.Copy(bootstrapCssPath, destinationCssPath, true);


                // Step 2: Extract HTML content from the main div
                string mainContentHtml = "";
                string head = "";
                try
                {

                    // var mainDiv = driver.FindElement(By.CssSelector($"div.format_tiles_section_content ul li[data-title='{activityName}']"));
                    // Remove all newline and carriage return characters from the activityName
                    activityName = activityName.Replace("\r", "").Replace("\n", "");

                    // Use the cleaned activityName in your XPath
                    var mainDiv = driver.FindElement(By.XPath($"//div[@class='format_tiles_section_content']//ul//li[@data-id=\"{id}\"]"));

                    mainContentHtml = mainDiv.GetAttribute("outerHTML");


                    int headStartIndex = pageHtml.IndexOf("<head");  // Find the start of the <head> tag
                    int headEndTagIndex = pageHtml.IndexOf(">", headStartIndex);  // Find the end of the opening <head> tag
                    int headCloseTagIndex = pageHtml.IndexOf("</head>");  // Find the closing </head> tag

                    if (headStartIndex != -1 && headEndTagIndex != -1 && headCloseTagIndex != -1)
                    {
                        // Extract the content between the <head> and </head> tags
                        head = pageHtml.Substring(headStartIndex, (headCloseTagIndex + 7) - headStartIndex);
                    }
                    else
                    {
                        Console.WriteLine("Head tag not found in the HTML document.");
                    }


                }
                catch (Exception ex)
                {

                    throw ex;
                }

                string bootstrapCssLink = $"<link rel=\"stylesheet\" href=\"{Path.Combine("styles", "bootstrap.min.css") + SasToken}\">";
                head += bootstrapCssLink;

                // Add meta charset tag and combine the modified HTML content with the main content
                pageHtml = "<html><head><meta charset=\"UTF-8\">" + head + "</head><body>" + mainContentHtml + "</body></html>";

                pageHtml = await DownloadAndReplaceImages(driver, pageHtml, imagesDirectory);
                // Remove all button elements from pageHtml
                pageHtml = Regex.Replace(pageHtml, @"<button\b[^>]*>(.*?)<\/button>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                // Match <img> tags with a width attribute
                // Match <img> tags with a width attribute
                pageHtml = Regex.Replace(
                     pageHtml,
                     @"<img\b([^>]*?)\bwidth\s*=\s*[""'](\d+)[""']([^>]*?)>",
                     m =>
                     {
                         // Extract the width value from the match
                         int widthValue = int.Parse(m.Groups[2].Value);
                         // If width is greater than 400, add style width:100%; height:auto; otherwise keep it as is
                         return widthValue > 800
                             ? $"<img{m.Groups[1].Value}width=\"{widthValue}\"{m.Groups[3].Value} style=\"width:100%; height:auto;\">"
                             : m.Value;
                     },
                     RegexOptions.IgnoreCase
                );


                pageHtml = Regex.Replace(pageHtml, @"<a\b([^>]*?)href\s*=\s*(['""])(.*?)\2([^>]*?)>", "<a$1href=\"#\"$4>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                //pageHtml = await DownloadImagesAndUpdatePaths(driver, pageHtml, imagesDirectory);
                pageHtml = await DownloadVideosAndUpdatePaths(driver, pageHtml, imagesDirectory);

                string activityName_ = SanitizeFileName(activityName);


                // Save the final HTML content to a file
                string pageFilePath = Path.Combine(sectionDirectory, $"{activityName_.Replace(" ", "") + "_" + order + "_label"}.html");


                await System.IO.File.WriteAllTextAsync(pageFilePath, pageHtml);

            }
            catch (Exception ex)
            {

                throw ex;
            }

        }
        private async Task DownloadDescriptionContent(IWebDriver driver, string sectionDirectory, string activityName)
        {
                string mainContentHtml = "";
                string pageHtml = driver.PageSource;

                try
                {
                    // Attempt to find the summary element
                    var mainDiv = driver.FindElement(By.CssSelector("div.format_tiles_section_content .summary"));
                    string mainDivContent = mainDiv.GetAttribute("innerHTML").Trim();

                    if (string.IsNullOrEmpty(mainDivContent))
                    {
                        Console.WriteLine("The <div class=\"summary\"></div> is empty, returning.");
                        // Exit the method or loop since the div has no content
                    }
                    else
                    {
                        mainContentHtml = mainDiv.GetAttribute("outerHTML");
                        //// Extract the <head> section from pageHtml
                        //int headStartIndex = pageHtml.IndexOf("<head");  // Find the start of the <head> tag
                        //int headEndTagIndex = pageHtml.IndexOf(">", headStartIndex);  // Find the end of the opening <head> tag
                        //int headCloseTagIndex = pageHtml.IndexOf("</head>");  // Find the closing </head> tag

                        //if (headStartIndex != -1 && headEndTagIndex != -1 && headCloseTagIndex != -1)
                        //{
                        //    // Extract the content between the <head> and </head> tags
                        //    pageHtml = pageHtml.Substring(headStartIndex, (headCloseTagIndex + 7) - headStartIndex);
                        //}
                        //else
                        //{
                        //    Console.WriteLine("Head tag not found in the HTML document.");
                        //}

                        // Add meta charset tag and combine the modified HTML content with the main content
                        pageHtml = "<html><head><meta charset=\"UTF-8\"></head><body>" + mainContentHtml + "</body></html>";

                        // Save the final HTML content to a file
                        string pageFilePath = Path.Combine(sectionDirectory, $"{activityName}.html");
                        await System.IO.File.WriteAllTextAsync(pageFilePath, pageHtml);
                    
                    }
                   
                }
                catch (NoSuchElementException)
                {
                    // Log or handle the case where .summary is not found
                    Console.WriteLine(".summary element not found, proceeding with head extraction.");
                }

        }

        private async Task<string> DownloadAndReplaceResources(IWebDriver driver, string htmlContent, string scriptsDirectory, string stylesDirectory, string imagesDirectory)
        {
            // Patterns to match script, link, img, favicon, and any URL starting with https://m3.inpt.ac.ma/
            string scriptPattern = @"<script.*?src=[""'](.*?)[""'].*?></script>";
            string cssPattern = @"<link[^>]*href=['""](?<url>.*?)['""](?:[^>]*charset=['""]utf-8['""])?[^>]*>";
            string imgPattern = @"<img.*?src=[""'](.*?)[""'].*?>";
            string faviconPattern = @"<link.*?rel=[""']shortcut icon[""'].*?href=[""'](.*?)[""'].*?/>";
            string generalPattern = @"https://m3.inpt.ac.ma/.*?[""'\s>]"; // Matches any URL starting with https://m3.inpt.ac.ma/

            // Download and replace scripts
            htmlContent = await DownloadAndReplace(driver, htmlContent, scriptPattern, "src", scriptsDirectory);

            // Download and replace CSS files
            htmlContent = await DownloadAndReplace(driver, htmlContent, cssPattern, "href", stylesDirectory);

            // Download and replace images
            htmlContent = await DownloadAndReplaceImages(driver, htmlContent, imagesDirectory);

            // Download and replace favicon
            //htmlContent = await DownloadAndReplace(driver, htmlContent, faviconPattern, "href", imagesDirectory);

            // Download and replace any general resources starting with https://m3.inpt.ac.ma/
            //htmlContent = await DownloadAndReplace(driver, htmlContent, generalPattern, null, imagesDirectory);

            return htmlContent;
        }

      


        private async Task<string> DownloadAndReplaceImages(IWebDriver driver, string htmlContent, string imagesDirectory)
        {
            var matches = Regex.Matches(htmlContent, @"<img.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string url = match.Groups[1].Value;
                if (url.StartsWith("//"))
                {
                    url = "https:" + url;
                }
                if (!url.StartsWith("http"))
                {
                    // Handle relative URLs by making them absolute
                    url = "https://m3.inpt.ac.ma" + url;
                }

                try
                {
                    // Open the image URL in a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());
                    driver.Navigate().GoToUrl(url);

                    // Wait for the image to load
                    await Task.Delay(2000);

                    // Get the file name
                    Uri uri = new Uri(url);
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string downloadsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                    string tempFilePath = Path.Combine(downloadsDirectory, fileName);

                    // Trigger download using JavaScript
                    ((IJavaScriptExecutor)driver).ExecuteScript($@"
                var link = document.createElement('a');
                link.href = '{url}';
                link.download = '{fileName}';
                document.body.appendChild(link);
                link.click();
                document.body.removeChild(link);");

                    // Wait for the file to appear in the Downloads folder
                    await WaitForFileToDownload(tempFilePath);

                    // Move the file to the desired directory
                    string finalFilePath = Path.Combine(imagesDirectory, fileName);
                    System.IO.File.Move(tempFilePath, finalFilePath);

                    // Replace the URL in the HTML with the relative path
                    string relativePath = Path.Combine(Path.GetFileName(imagesDirectory), fileName).Replace("\\", "/");
                    htmlContent = htmlContent.Replace(url, relativePath + SasToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download image: {url}. Error: {ex.Message}");
                }
                finally
                {
                    // Close the current tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());
                }
            }

            return htmlContent;
        }
        private async Task WaitForFileToDownload(string filePath)
        {
            int attempts = 0;
            while (!System.IO.File.Exists(filePath) && attempts < 30)
            {
                await Task.Delay(1000); // Wait for 1 second
                attempts++;
            }

            if (attempts >= 30)
            {
                throw new TimeoutException("File download timed out.");
            }
        }
        private async Task<string> DownloadAndReplace(IWebDriver driver, string htmlContent, string pattern, string attribute, string directory)
        {
            var matches = Regex.Matches(htmlContent, pattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string url;
                if (attribute != null)
                {
                    url = match.Groups[1].Value;
                }
                else
                {
                    // For the general pattern, extract the full match
                    url = match.Value.TrimEnd('"', '\'', ' ', '>');
                }

                if (!url.StartsWith("http") && !url.StartsWith("//") && !url.StartsWith("https://m3.inpt.ac.ma/"))
                {
                    // Skip if the URL is not an absolute, protocol-relative URL, or doesn't start with the specified base URL
                    continue;
                }

                // Handle protocol-relative URLs
                if (url.StartsWith("//"))
                {
                    url = "https:" + url;
                }

                try
                {
                    // Open the resource in a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());
                    driver.Navigate().GoToUrl(url);

                    // Wait for the page/resource to load
                    await Task.Delay(2000);

                    // Get the file name and save path
                    Uri uri = new Uri(url);
                    string fileName = Path.GetFileName(uri.LocalPath);

                    // Check if the file has no extension and is likely a CSS file
                    if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                    {
                        fileName += ".css"; // Add .css extension
                    }

                    string filePath = Path.Combine(directory, fileName);

                    // Download the resource using HttpClient
                    using (HttpClient client = new HttpClient())
                    {
                        var resourceBytes = await client.GetByteArrayAsync(url);
                        await System.IO.File.WriteAllBytesAsync(filePath, resourceBytes);
                    }

                    // Replace the URL in the HTML with the relative path
                    string relativePath = Path.Combine(Path.GetFileName(directory), fileName).Replace("\\", "/");
                    htmlContent = htmlContent.Replace(url, relativePath + SasToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download resource: {url}. Error: {ex.Message}");
                }
                finally
                {
                    // Close the current tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());
                }
            }

            return htmlContent;
        }

        private async Task<string> DownloadImagesAndUpdatePaths(IWebDriver driver, string htmlContent, string sectionDirectory)
        {
            var matches = Regex.Matches(htmlContent, @"<img.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string imageUrl = match.Groups[1].Value;

                if (!imageUrl.StartsWith("http"))
                {
                    imageUrl = "https://m3.inpt.ac.ma" + imageUrl;
                }

                try
                {
                    // Get the file name from the URL
                    Uri uri = new Uri(imageUrl);
                    string fileName = Path.GetFileName(uri.LocalPath);

                    // Check if the file has no extension and is likely a CSS file
                    if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                    {
                        fileName += ".css"; // Add .css extension
                    }

                    string filePath = Path.Combine(sectionDirectory, fileName);

                    // Open a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());

                    // Navigate to the image URL
                    driver.Navigate().GoToUrl(imageUrl);

                    // Wait for the image to load
                    await Task.Delay(2000);

                    // Use HttpClient with cookie handling to download the image
                    using (var handler = new HttpClientHandler())
                    {
                        var cookies = driver.Manage().Cookies.AllCookies;
                        handler.CookieContainer = new CookieContainer();

                        foreach (var cookie in cookies)
                        {
                            handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                        }

                        using (HttpClient client = new HttpClient(handler))
                        {
                            var imageBytes = await client.GetByteArrayAsync(imageUrl);
                            await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);
                        }
                    }

                    // Close the new tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());

                    // Replace the URL in the HTML with the relative path
                    string relativePath = fileName; // Since the image will be in the same directory
                    htmlContent = htmlContent.Replace(imageUrl, relativePath + SasToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download image: {imageUrl}. Error: {ex.Message}");
                }
            }

            return htmlContent;
        }

        private async Task<string> DownloadVideosAndUpdatePaths(IWebDriver driver, string htmlContent, string sectionDirectory)
        {
            var matches = Regex.Matches(htmlContent, @"<video.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string videoUrl = match.Groups[1].Value;

                if (!videoUrl.StartsWith("http"))
                {
                    videoUrl = "https://m3.inpt.ac.ma" + videoUrl;
                }

                try
                {
                    // Get the file name from the URL
                    Uri uri = new Uri(videoUrl);
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string filePath = Path.Combine(sectionDirectory, fileName);

                    // Open a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());

                    // Navigate to the video URL
                    driver.Navigate().GoToUrl(videoUrl);

                    // Wait for the video to load
                    await Task.Delay(2000);

                    // Use HttpClient with cookie handling to download the video
                    using (var handler = new HttpClientHandler())
                    {
                        var cookies = driver.Manage().Cookies.AllCookies;
                        handler.CookieContainer = new CookieContainer();

                        foreach (var cookie in cookies)
                        {
                            handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                        }

                        using (HttpClient client = new HttpClient(handler))
                        {
                            var videoBytes = await client.GetByteArrayAsync(videoUrl);
                            await System.IO.File.WriteAllBytesAsync(filePath, videoBytes);
                        }
                    }

                    // Close the new tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());

                    // Replace the URL in the HTML with the relative path
                    string relativePath = fileName; // Since the video will be in the same directory
                    htmlContent = htmlContent.Replace(videoUrl, relativePath + SasToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download video: {videoUrl}. Error: {ex.Message}");
                }
            }

            return htmlContent;
        }

        private async Task DownloadTestContent(IWebDriver driver, string testUrl, string sectionDirectory, string activityName)
        {

            driver.Navigate().GoToUrl(testUrl);
            await Task.Delay(2000); // Wait for the page to load

            try
            {
                var continueButton = driver.FindElement(By.CssSelector("button.btn.btn-primary"));
                continueButton.Click();
                await Task.Delay(2000); // Wait for the test page to load

                string testPageHtml = driver.PageSource;

                // Remove unwanted HTML elements
                testPageHtml = RemoveUnwantedHtmlElements(testPageHtml);

                testPageHtml = Regex.Replace(testPageHtml, @"<div[^>]*class=['""][^'""]*theme-coursenav[^'""]*['""][^>]*>.*?</div>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);


                // Create directories for scripts, styles, and images
                string scriptsDirectory = Path.Combine(sectionDirectory, "scripts");
                string stylesDirectory = Path.Combine(sectionDirectory, "styles");
                string imagesDirectory = Path.Combine(sectionDirectory, "images");

                Directory.CreateDirectory(scriptsDirectory);
                Directory.CreateDirectory(stylesDirectory);
                Directory.CreateDirectory(imagesDirectory);

                // Download and replace external resources
                testPageHtml = await DownloadAndReplaceResources(driver, testPageHtml, scriptsDirectory, stylesDirectory, imagesDirectory);

                // Save the modified HTML content to a file
                string testFilePath = Path.Combine(sectionDirectory, $"{activityName}.html");
                await System.IO.File.WriteAllTextAsync(testFilePath, testPageHtml);
            }
            catch (NoSuchElementException)
            {
                // Handle the case where the continue button is not found
            }
        }

        private string RemoveUnwantedHtmlElements(string htmlContent)
        {
            // Load the HTML document
            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(htmlContent);

            // Define a list of XPath expressions for elements to be hidden
            string[] xpathsToHide = {
                "//div[@id='fsmod-header']",
                "//div[@class='container-fluid tertiary-navigation']",
                "//div[@class='theme-coursenav flexcols onlynext']",
                "//div[@id='course-panel']",
                "//div[@id='fsmod-sidebar']",
                "//div[@class='activity-header']",
                "//div[@class='submitbtns']",
                "//div[@class='theme-coursenav flexcols onlynext']"
            };

            // Hide the elements by adding a style attribute with 'display: none;'
            foreach (string xpath in xpathsToHide)
            {
                var nodesToHide = document.DocumentNode.SelectNodes(xpath);
                if (nodesToHide != null)
                {
                    foreach (var node in nodesToHide)
                    {
                        // Check if the node already has a 'style' attribute
                        var styleAttribute = node.Attributes["style"];
                        if (styleAttribute == null)
                        {
                            // If not, add a new 'style' attribute
                            node.Attributes.Add("style", "display: none;");
                        }
                        else
                        {
                            // If 'style' attribute exists, append 'display: none;' to it
                            styleAttribute.Value += " display: none;";
                        }
                    }
                }
            }

            // Return the modified HTML content
            return document.DocumentNode.OuterHtml;
        }


        private async Task DownloadScormContent(IWebDriver driver, string scormpUrl, string sectionDirectory, string activityName)
        {
            // Open a new tab and switch to it
            ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
            driver.SwitchTo().Window(driver.WindowHandles.Last());

            // Navigate to the H5P content URL in the new tab
            driver.Navigate().GoToUrl(scormpUrl);
            await Task.Delay(2000); // Wait for the page to load
          }

        private async Task DownloadH5PContent(IWebDriver driver, string h5pUrl, string sectionDirectory, string activityName)
        {
            // Open a new tab and switch to it
            ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
            driver.SwitchTo().Window(driver.WindowHandles.Last());

            // Navigate to the H5P content URL in the new tab
            driver.Navigate().GoToUrl(h5pUrl);
            await Task.Delay(3000); // Wait for the page to load

            var exportUrls = new List<string>();

            // Get all <script> elements
            var scriptElements = driver.FindElements(By.TagName("script"));

            foreach (var scriptElement in scriptElements)
            {
                try
                {
                    var scriptText = scriptElement.GetAttribute("innerHTML");
                    if (scriptText.Contains("var H5PIntegration = "))
                    {
                        string startPattern = "\r\n//<![CDATA[\r\nvar H5PIntegration = ";
                        string endPattern = ";\r\n//]]>";

                        int startIndex = scriptText.IndexOf(startPattern);

                        if (startIndex != -1)
                        {
                            startIndex += startPattern.Length; // Move past the start pattern

                            // Find the next semicolon after startIndex
                            int endIndex = scriptText.IndexOf(endPattern, startIndex);

                            if (endIndex != -1)
                            {
                                // Extract the JSON substring
                                string jsonSubstring = scriptText.Substring(startIndex, endIndex - startIndex).Trim();

                                try
                                {
                                    // Deserialize JSON substring into JsonNode
                                    JsonNode jsonObject = JsonNode.Parse(jsonSubstring);
                                    FindExportUrls(jsonObject["contents"], exportUrls);

                                    if (exportUrls.Count > 0)
                                    {
                                        string exportUrl = exportUrls[0];
                                        if (!string.IsNullOrEmpty(exportUrl))
                                        {
                                            // Define the new H5P folder structure
                                            //string h5pRootDirectory = Path.Combine("H5P", SanitizeFileName(activityName));
                                            //Directory.CreateDirectory(h5pRootDirectory);

                                            // Download the file from the export URL using the authenticated session
                                            string h5pFilePath = await DownloadFileFromUrlWithCookies(driver, exportUrl, sectionDirectory);

                                            // Unzip the .h5p file into the H5P directory
                                            if (!string.IsNullOrEmpty(h5pFilePath) && System.IO.File.Exists(h5pFilePath))
                                            {
                                                UnzipH5PFile(h5pFilePath, sectionDirectory);
                                                // Optionally delete the original .h5p file after extraction
                                                System.IO.File.Delete(h5pFilePath);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("Error parsing JSON: " + ex.Message);
                                }
                            }
                            else
                            {
                                Console.WriteLine("End pattern not found.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Start pattern not found.");
                        }

                        break;
                    }

                }
                catch (Exception ex)
                {

                    continue;
                }
            }

            // Close the current tab
            driver.Close();

            // Switch back to the original tab
            driver.SwitchTo().Window(driver.WindowHandles.First());
        }

        private async Task DownloadGeoGebraContent(IWebDriver driver, string geoGebraUrl, string sectionDirectory, string activityName)
        {
            // Open a new tab and switch to it
            ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
            driver.SwitchTo().Window(driver.WindowHandles.Last());

            // Navigate to the GeoGebra content URL in the new tab
            driver.Navigate().GoToUrl(geoGebraUrl);
            await Task.Delay(2000); // Wait for the page to load

            try
            {
                // Extract the HTML content from the no-overflow div
                var noOverflowDiv = driver.FindElement(By.CssSelector("div.no-overflow"));
                string geoGebraHtml = noOverflowDiv.GetAttribute("outerHTML");

                // Save the HTML content to a file
                string geoGebraFilePath = Path.Combine(sectionDirectory, $"{activityName}_geogebra.html");
                await System.IO.File.WriteAllTextAsync(geoGebraFilePath, geoGebraHtml);

                // Extract the ggbBase64 value from the script elements
                var scriptElements = driver.FindElements(By.TagName("script"));
                foreach (var scriptElement in scriptElements)
                {
                    var scriptText = scriptElement.GetAttribute("innerHTML");
                    if (scriptText.Contains("ggbBase64"))
                    {
                        string base64Pattern = @"ggbBase64:\s*\""([^\""]+)\""";

                        var match = Regex.Match(scriptText, base64Pattern);
                        if (match.Success)
                        {
                            string base64String = match.Groups[1].Value;
                            string base64FilePath = Path.Combine(sectionDirectory, $"{activityName}_geogebra.txt");
                            await System.IO.File.WriteAllTextAsync(base64FilePath, base64String);
                        }
                        break;
                    }
                }
            }
            finally
            {
                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
        }

        private async Task DownloadPdfFile(IWebDriver driver, string fileUrl, string directory)
        {
            using (var handler = new HttpClientHandler())
            {
                // Get cookies from the Selenium WebDriver
                var cookies = driver.Manage().Cookies.AllCookies;
                handler.CookieContainer = new CookieContainer();

                foreach (var cookie in cookies)
                {
                    handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                }

                using (var client = new HttpClient(handler))
                {

                    var fileName = Path.GetFileName(fileUrl);
                    var decodedFileName = SanitizeFileName(fileName);
                    var filePath = Path.Combine(directory, decodedFileName);

                    var pdfBytes = await client.GetByteArrayAsync(fileUrl);
                    await System.IO.File.WriteAllBytesAsync(filePath, pdfBytes);

                    Console.WriteLine($"PDF downloaded to: {filePath}");
                }
            }
        }

        private string SanitizeFileName(string fileName)
        {
            string decodedFileName = Uri.UnescapeDataString(fileName);

            return string.Join("_", decodedFileName.Split(Path.GetInvalidFileNameChars()));
        }

        private void FindExportUrls(JsonNode node, List<string> exportUrls)
        {
            if (node is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "exportUrl" && kvp.Value is JsonValue val)
                    {
                        exportUrls.Add(val.ToString());
                    }
                    else if (kvp.Value is JsonObject || kvp.Value is JsonArray)
                    {
                        FindExportUrls(kvp.Value, exportUrls);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    FindExportUrls(item, exportUrls);
                }
            }
        }


        private void FindContentUrls(JsonNode node, List<string> exportUrls)
        {
            if (node is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "contentUrl" && kvp.Value is JsonValue val)
                    {
                        exportUrls.Add(val.ToString());
                    }
                    else if (kvp.Value is JsonObject || kvp.Value is JsonArray)
                    {
                        FindExportUrls(kvp.Value, exportUrls);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    FindExportUrls(item, exportUrls);
                }
            }
        }

        private async Task<string> DownloadFileFromUrlWithCookies(IWebDriver driver, string fileUrl, string directory)
        {
            using (var handler = new HttpClientHandler())
            {
                // Get cookies from the Selenium WebDriver
                var cookies = driver.Manage().Cookies.AllCookies;
                handler.CookieContainer = new CookieContainer();

                foreach (var cookie in cookies)
                {
                    handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                }

                using (var client = new HttpClient(handler))
                {
                    try
                    {
                        var fileName = Path.GetFileName(fileUrl);

                        // Check if the file has no extension and is likely a CSS file
                        if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                        {
                            fileName += ".css"; // Add .css extension
                        }

                        var filePath = Path.Combine(directory, fileName);

                        var fileBytes = await client.GetByteArrayAsync(fileUrl);
                        await System.IO.File.WriteAllBytesAsync(filePath, fileBytes);

                        Console.WriteLine($"File downloaded to: {filePath}");
                        return filePath;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to download file from URL: {fileUrl}. Error: {ex.Message}");
                        return null;
                    }
                }
            }
        }

        private void UnzipH5PFile(string h5pFilePath, string destinationDirectory)
        {
            try
            {
                // Get the file name without extension to create a folder
                string folderName = Path.GetFileNameWithoutExtension(h5pFilePath);
                string extractionPath = Path.Combine(destinationDirectory, folderName);

                // Create the directory
                Directory.CreateDirectory(extractionPath);

                // Extract the contents of the .h5p file into the created folder
                ZipFile.ExtractToDirectory(h5pFilePath, extractionPath);
                Console.WriteLine($"Extracted .h5p file to: {extractionPath}");

                // Optional: Create an index.html for easy access or viewing
                //CreateIndexHtml(extractionPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to unzip .h5p file: {h5pFilePath}. Error: {ex.Message}");
            }
        }

        private void CreateIndexHtml(string directory)
        {
            string indexPath = Path.Combine(directory, "index.html");
            string htmlContent = "<html><body><h1>H5P Content</h1></body></html>";

            System.IO.File.WriteAllText(indexPath, htmlContent);
            Console.WriteLine($"Created index.html at: {indexPath}");
        }
        /*   private async Task UploadToBlobStorageAsync()
           {
               string connectionString = "DefaultEndpointsProtocol=https;AccountName=stppncours;AccountKey=mqsGyKxuR2tnJ5RpqRjR/wTgKlrVCFo4mV2H3vAlTuvmoYY5jRMxQ/UkQqCGLO5yOXwTXLYEcm+p+AStwCbyjQ==;EndpointSuffix=core.windows.net";

               BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

               // Upload 1ere APIC to the 'courses' container
               string coursesContainerName = "courses";
               BlobContainerClient coursesContainerClient = blobServiceClient.GetBlobContainerClient(coursesContainerName);
               await coursesContainerClient.CreateIfNotExistsAsync();

               string coursesDirectoryPath = "1ere APIC";
               await UploadDirectoryToBlobContainer(coursesContainerClient, coursesDirectoryPath, coursesDirectoryPath);

               // Upload H5P to the 'h5p' container
               string h5pContainerName = "h5p";
               BlobContainerClient h5pContainerClient = blobServiceClient.GetBlobContainerClient(h5pContainerName);
               await h5pContainerClient.CreateIfNotExistsAsync();

               string h5pDirectoryPath = "H5P";
               await UploadDirectoryToBlobContainer(h5pContainerClient, h5pDirectoryPath, h5pDirectoryPath);
           }

           private async Task UploadDirectoryToBlobContainer(BlobContainerClient containerClient, string directoryPath, string baseDirectoryPath)
           {
               foreach (var filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
               {
                   string blobName = filePath.Substring(baseDirectoryPath.Length + 1).Replace("\\", "/");
                   BlobClient blobClient = containerClient.GetBlobClient(blobName);

                   using FileStream fileStream = System.IO.File.OpenRead(filePath);
                   await blobClient.UploadAsync(fileStream, overwrite: true);
                   Console.WriteLine($"Uploaded {blobName} to {containerClient.Name} container.");
               }
           }*/
        private async Task UploadToBlobStorageAsync(string coursesPath)
        {
            string connectionString = "DefaultEndpointsProtocol=https;AccountName=stppncours;AccountKey=mqsGyKxuR2tnJ5RpqRjR/wTgKlrVCFo4mV2H3vAlTuvmoYY5jRMxQ/UkQqCGLO5yOXwTXLYEcm+p+AStwCbyjQ==;EndpointSuffix=core.windows.net";

            BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

            // Create blob clients for 'courses' and 'h5p' containers
            string coursesContainerName = "courses";
            BlobContainerClient coursesContainerClient = blobServiceClient.GetBlobContainerClient(coursesContainerName);
            await coursesContainerClient.CreateIfNotExistsAsync();

            string h5pContainerName = "h5p";
            BlobContainerClient h5pContainerClient = blobServiceClient.GetBlobContainerClient(h5pContainerName);
            await h5pContainerClient.CreateIfNotExistsAsync();

            // Process and upload directories
            await ProcessAndUploadDirectories(coursesContainerClient, h5pContainerClient, coursesPath);
        }

        private async Task ProcessAndUploadDirectories(BlobContainerClient coursesContainerClient, BlobContainerClient h5pContainerClient, string baseDirectoryPath)
        {
            foreach (var directoryPath in Directory.GetDirectories(baseDirectoryPath, "*", SearchOption.AllDirectories))
            {
                // Check if any child directory (not this one) contains h5p.json
                if (ContainsH5PJsonInSubdirectories(directoryPath))
                {
                    // If any child directory contains h5p.json, upload only those specific child directories to 'h5p' container
                    foreach (var subDirectory in Directory.GetDirectories(directoryPath))
                    {
                        if (System.IO.File.Exists(Path.Combine(subDirectory, "h5p.json")))
                        {
                            await UploadDirectoryOrFileToBlobContainer(h5pContainerClient, subDirectory, baseDirectoryPath);
                        }
                    }
                }
                else
                {
                    // Otherwise, upload the current directory to the 'courses' container
                    await UploadDirectoryOrFileToBlobContainer(coursesContainerClient, directoryPath, baseDirectoryPath);
                }
            }
        }
        private bool ContainsH5PJsonInSubdirectories(string directoryPath)
        {
            // Recursively check all subdirectories for the presence of h5p.json
            foreach (var subDirectory in Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories))
            {
                if (System.IO.File.Exists(Path.Combine(subDirectory, "h5p.json")))
                {
                    return true;
                }
            }

            return false;
        }
        private async Task UploadDirectoryOrFileToBlobContainer(BlobContainerClient containerClient, string path, string baseDirectoryPath)
        {
            if (System.IO.File.Exists(path))
            {
                // If the path is a file, upload it directly
                await UploadFileToBlob(containerClient, path, baseDirectoryPath);
            }
            else if (Directory.Exists(path))
            {
                // If the path is a directory, recursively upload all files within it
                foreach (var filePath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    await UploadFileToBlob(containerClient, filePath, baseDirectoryPath);
                }
            }
            else
            {
                Console.WriteLine($"The provided path '{path}' is neither a file nor a directory.");
            }
        }

        // Helper method to upload a single file
        private async Task UploadFileToBlob(BlobContainerClient containerClient, string filePath, string baseDirectoryPath)
        {
            try
            {
                // Replace backslashes with forward slashes for the blob name
                string blobName = filePath.Replace("\\", "/");
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                string mimeType = GetMimeType(filePath);

                // Set the MIME type using BlobHttpHeaders
                var blobHttpHeaders = new BlobHttpHeaders
                {
                    ContentType = mimeType
                };

                using FileStream fileStream = System.IO.File.OpenRead(filePath);

                // Upload the file with the specified MIME type
                await blobClient.UploadAsync(fileStream, new BlobUploadOptions
                {
                    HttpHeaders = blobHttpHeaders
                });

                Console.WriteLine($"Uploaded {blobName} to {containerClient.Name} container with MIME type {mimeType}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to upload file {filePath}. Error: {ex.Message}");
            }
        }

        /*private async Task UploadDirectoryToBlobContainer(BlobContainerClient containerClient, string directoryPath, string baseDirectoryPath)
        {
            foreach (var filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                string blobName = filePath.Replace("\\", "/");
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                string mimeType = GetMimeType(filePath);

                // Set the MIME type using BlobHttpHeaders
                var blobHttpHeaders = new BlobHttpHeaders
                {
                    ContentType = mimeType
                };

                using FileStream fileStream = System.IO.File.OpenRead(filePath);

                // Upload the file with the specified MIME type
                await blobClient.UploadAsync(fileStream, new BlobUploadOptions
                {
                    HttpHeaders = blobHttpHeaders
                });

                Console.WriteLine($"Uploaded {blobName} to {containerClient.Name} container with MIME type {mimeType}.");
            }
        }*/
        private static string GetMimeType(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".html" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".json" => "application/json",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".xml" => "application/xml",
                ".zip" => "application/zip",
                ".pdf" => "application/pdf",  // Added MIME type for PDF files
                _ => "application/octet-stream", // Default MIME type for unknown file types
            };
        }

        private bool AddH5PContentsRecursively(string baseDirectory, Element sectionElement, string courseName, string sectionName)
        {
            // Recursively search for h5p.json files in all subdirectories
            foreach (var subDirectory in Directory.GetDirectories(baseDirectory))
            {
                if (System.IO.File.Exists(Path.Combine(subDirectory, "h5p.json")))
                {
                    var dossier = subDirectory.Replace("\\", "/");
                    // Create content for H5P
                    var content = new Content
                    {
                        ContentName = Path.GetFileName(subDirectory),
                        Type = "H5P",
                        Files = new FilesModel
                        {
                            FilesH5p = new List<string> { $"/h5p/{dossier}" }
                        }

                    };
                    sectionElement.contents.Add(content);
                    return true;

                }
                else
                {
                    // Recursively check further subdirectories

                    foreach (var subDir2 in Directory.GetDirectories(subDirectory))
                    {
                        if (System.IO.File.Exists(Path.Combine(subDir2, "h5p.json")))
                        {

                            var dossier = subDirectory.Replace("\\", "/");
                            // Create content for H5P
                            var content = new Content
                            {
                                ContentName = Path.GetFileName(subDirectory),
                                Type = "H5P",
                                Files = new FilesModel
                                {
                                    FilesH5p = new List<string> { $"/h5p/{dossier}" }
                                }
                            };
                            sectionElement.contents.Add(content);
                            return true;
                        }
                        // AddH5PContentsRecursively(subDir2, sectionElement, courseName, sectionName);

                    }
                }
                return false;
            }
            return false;
        }

        private static string GetRelativePath(string baseDirectory, string subDirectory)
        {
            // Generate relative path for URLs
            try
            {
                string uriCompatibleBaseDirectory = baseDirectory.Replace("\\", "/");
                string uriCompatiblesubDirectory = subDirectory.Replace("\\", "/");

                Uri baseUri = new Uri(uriCompatibleBaseDirectory, UriKind.RelativeOrAbsolute);
                Uri subUri = new Uri(uriCompatiblesubDirectory, UriKind.RelativeOrAbsolute);
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(subUri).ToString().Replace('/', Path.DirectorySeparatorChar));
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return "";
            }

        }

        private async Task GenerateCourseJson(string courseName, string courseDirectory, string? courseImagePath, List<string> sections, List<ElementProgramme> elementProgrammes)
        {
            var courseId = Guid.NewGuid().ToString();

            string? photoAbsolutePath = Directory.GetFiles(courseDirectory, "*.*", SearchOption.AllDirectories)
                  .FirstOrDefault(file => file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                          file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                          file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                          file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))?.Replace("\\", "/");
            string? photoRelativePath = photoAbsolutePath != null ? Path.GetRelativePath(courseDirectory, photoAbsolutePath).Replace("\\", "/") : null;

            string description = photoAbsolutePath?.Split('/')[1] ?? "";

            var courseJson = new CourseJson
            {
                CourseName = courseName,
                CourseId = courseId,
                Photo = "/courses/" + photoAbsolutePath, // Use the first found image path
                //ProfessorId = "dfe910f4-c065-46cd-b23c-265ddc85a8ed", // Static for now
                CodeNiveau = "2A32101010", // Static for now
                //CodeClasse = "9210d27e-ec40-4568-9fbe-40146f9f1c2f", // Static for now
                ElementProgrammes = elementProgrammes,
                Description = description,
                Elements = new List<Element>(),
                id = courseId // Add the 'id' property for Cosmos DB
            };

            int elementId = 1;
            foreach (var section in sections)
            {
                string sectionName = Path.GetFileName(section);

                string dataSection = sectionName.Split("-").Last();
                string sectionDirectory = Path.Combine(courseDirectory, SanitizeFileName(sectionName));
                int lastHyphenIndex = sectionName.LastIndexOf('-');

                // Check if there is a hyphen to remove
                if (lastHyphenIndex != -1)
                {
                    sectionName = sectionName.Substring(0, lastHyphenIndex); // This will give "section"
                }
                var sectionElement = new Element
                {
                    ElementName = sectionName,
                    ElementId = elementId.ToString(),
                    DataSection = dataSection,
                    CourseId = courseId,
                    IconBody = "<i class=\"ki-duotone text-primary ki-book-open fs-3x\"><span class=\"path1\"></span><span class=\"path2\"></span><span class=\"path3\"></span><span class=\"path4\"></span></i>",
                    Link = $"/courses/{courseName}/{SanitizeFileName(sectionName)}",
                    IsDownloaded = false,
                    contents = new List<Content>(),
                    FileDescription=""
                };

               
                // Recursively add H5P contents
                var h5pfound = AddH5PContentsRecursively(sectionDirectory, sectionElement, courseName, sectionName);
                //if (h5pfound)
                //{
                //    courseJson.Elements.Add(sectionElement);
                //    elementId++;
                //    continue;
                //}

                var content = new Content
                {
                    ContentName = "",
                    Files = new FilesModel()
                };

                // Fetch the contents based on downloaded files and types
                foreach (var filePath in Directory.GetFiles(sectionDirectory, "*", SearchOption.AllDirectories))
                {
                    var relativeFilePath = filePath.Replace("\\", "/");
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    string sanitizedFileNamePart = SanitizeFileName(fileName).Split("__")[0];
                    string type = sanitizedFileNamePart.Split("_").Last() ?? "";

                    // Check if the string contains an underscore
                    if (sanitizedFileNamePart.Contains("_") && type == "geogebra")
                    {
                        // Find the index of the last underscore
                        int lastUnderscoreIndex = sanitizedFileNamePart.LastIndexOf("_");

                        // Remove the part of the string after the last underscore
                        sanitizedFileNamePart = sanitizedFileNamePart.Substring(0, lastUnderscoreIndex);

                    }


                    var existContent = sectionElement.contents.Find(x => x.ContentName == sanitizedFileNamePart);

                    if (filePath.EndsWith("Description.html"))
                    {
                        if(sectionElement.FileDescription == "")
                            sectionElement.FileDescription = "/courses/" + relativeFilePath;
                        continue;
                    }


                    if (existContent == null)
                    {
                        content = new Content
                        {
                            ContentName = sanitizedFileNamePart,
                            Files = new FilesModel()
                        };

                        if (filePath.EndsWith(".html") && type == "label")
                        {
                            Match match = Regex.Match(sanitizedFileNamePart, @"(\d+)_label");
                            string order = match.Groups[1].Value;
                            var contentName_ = Regex.Replace(sanitizedFileNamePart, @"\s*\d+_label", "").Trim();
                            content.ContentName = Regex.Replace(contentName_, @"_$", "");
                            content.Type = "label";
                            content.order = order;
                            content.Files.FileHtml = "/courses/" + relativeFilePath;
                        }


                       else if (filePath.EndsWith(".html") && type != "geogebra" && type != "label")
                        {
                            content.Type = "html";
                            content.Files.FileHtml = "/courses/" + relativeFilePath;
                        }
                        else if (filePath.EndsWith(".pdf"))
                        {
                            content.Type = "file";
                            content.Files.FilePdf = "/courses/" + relativeFilePath;

                        }
                        else if (filePath.EndsWith(".mp4"))
                        {
                            content.Type = "media";
                            content.Files.FileMp4 = "/courses/" + relativeFilePath;
                        }
                        else if (type == "geogebra")
                        {
                            content.Type = type;
                            content.Width = "960";
                            content.Height = "560";
                            content.Files.FileHtml = !filePath.EndsWith(".html") ? "" : "/courses/" + Path.Combine(sectionDirectory, content.ContentName + "_geogebra.html").Replace("\\", "/");
                            content.Files.FileTxt = !filePath.EndsWith(".txt") ? "" : "/courses/" + Path.Combine(sectionDirectory, content.ContentName + "_geogebra.txt").Replace("\\", "/").ToString();
                        }
                        else if (Directory.Exists(filePath) && Path.GetFileName(filePath).EndsWith(".h5p"))
                        {
                            content.Type = "H5P";
                            content.Files.FilesH5p = Directory.GetFiles(filePath).Select(f => f.Replace("\\", "/").Replace(courseDirectory + "/", "")).ToList();
                        }
                        else
                        {
                            continue;
                        }
                        sectionElement.contents.Add(content);
                    }
                    else if (existContent != null && existContent.Files != null)
                    {
                        if (filePath.EndsWith(".html") && type == "label")
                        {
                            content = new Content
                            {
                                ContentName = sanitizedFileNamePart,
                                Files = new FilesModel()
                            };

                            Match match = Regex.Match(sanitizedFileNamePart, @"(\d+)_label");
                            string order = match.Groups[1].Value;
                            content.Type = "label";
                            content.order = order;
                            content.Files.FileHtml = "/courses/" + relativeFilePath;
                        }
                        else if (filePath.EndsWith(".html"))
                        {
                            existContent.Files.FileHtml = "/courses/" + Path.Combine(sectionDirectory, content.ContentName + (type == "geogebra" ? "_geogebra.html" : ".html")).Replace("\\", "/");
                        }
                        else if (filePath.EndsWith(".txt"))
                        {
                            existContent.Files.FileTxt = "/courses/" + Path.Combine(sectionDirectory, content.ContentName + (type == "geogebra" ? "_geogebra.txt" : ".txt")).Replace("\\", "/").ToString();
                        }
                    }

                }

                courseJson.Elements.Add(sectionElement);
                elementId++;
            }

            string jsonOutput = JsonSerializer.Serialize(courseJson, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            string jsonFilePath = Path.Combine(courseDirectory, $"{courseName}.json");

            await System.IO.File.WriteAllTextAsync(jsonFilePath, jsonOutput);

            Console.WriteLine($"Course JSON generated at: {jsonFilePath}");

            // Upload JSON to Cosmos DB
            await UploadJsonToCosmosDb(courseJson);
        }

        private async Task UploadJsonToCosmosDb(CourseJson courseJson)
        {
            try
            {
                await _cosmosContainer.CreateItemAsync(courseJson, new PartitionKey(courseJson.CourseId));
                Console.WriteLine($"Course JSON uploaded to Cosmos DB with CourseId: {courseJson.CourseId}");
            }
            catch (CosmosException ex)
            {
                Console.WriteLine($"Error uploading JSON to Cosmos DB: {ex.Message}");
            }




        }


        //private async Task UploadJsonToCosmosDb(CourseJson courseJson)
        //{
        //    try
        //    {
        //        // Query for the existing course based on CourseName
        //        var query = new QueryDefinition("SELECT * FROM c WHERE c.CourseName = @courseName")
        //            .WithParameter("@courseName", courseJson.CourseName);

        //        var iterator = _cosmosContainer.GetItemQueryIterator<CourseJson>(query);
        //        CourseJson existingCourse = null;

        //        // Fetch the existing course
        //        while (iterator.HasMoreResults)
        //        {
        //            var response = await iterator.ReadNextAsync();
        //            existingCourse = response.FirstOrDefault();
        //            if (existingCourse != null) break;
        //        }

        //        if (existingCourse == null)
        //        {
        //            Console.WriteLine($"No existing course found with CourseName: {courseJson.CourseName}");
        //            return;
        //        }

        //        // Filter contents with type "html" from incoming courseJson
        //        var newHtmlContents = courseJson.Elements?
        //            .SelectMany(e => e.contents ?? new List<Content>())
        //            .Where(c => c.Type == "html")
        //            .ToList() ?? new List<Content>();

        //        // Update existing course by adding new HTML contents to corresponding Elements
        //        foreach (var newElement in courseJson.Elements)
        //        {
        //            // Find the corresponding element in the existing course
        //            var existingElement = existingCourse.Elements?.FirstOrDefault(e => e.ElementName == newElement.ElementName);

        //            if (existingElement != null)
        //            {
        //                foreach (var newContent in newElement.contents.Where(c => c.Type == "html"))
        //                {
        //                    // Check if the contentName already exists in the corresponding element
        //                    if (!existingElement.contents.Any(c => c.ContentName == newContent.ContentName))
        //                    {
        //                        existingElement.contents.Add(newContent);
        //                    }
        //                }
        //            }
        //        }

        //        // Update the existing course in Cosmos DB
        //        await _cosmosContainer.ReplaceItemAsync(existingCourse, existingCourse.id, new PartitionKey(existingCourse.CourseId));
        //        Console.WriteLine($"Course JSON updated in Cosmos DB with CourseId: {existingCourse.CourseId}");
        //    }
        //    catch (CosmosException ex)
        //    {
        //        Console.WriteLine($"Error uploading JSON to Cosmos DB: {ex.Message}");
        //    }
        //}




    }
}