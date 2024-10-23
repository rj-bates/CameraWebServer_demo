using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;


namespace CameraFlashWebAppServer
{
    public class FlashControllerApi
    {
        private MediaCapture? _mediaCapture;
        private readonly Serilog.ILogger _logger;

        public FlashControllerApi(Serilog.ILogger logger)
        {
            _logger = logger;
        }
        public async Task<bool> InitializeAsync(CameraType cameraType)
        {
            try
            {
                if (_mediaCapture == null)
                {
                    _logger.Information("Initializing MediaCapture");
                   
                    var cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                    DeviceInformation cameraDevice = null;

                    if (cameraType == CameraType.BACK)
                    {
                        cameraDevice = cameraDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back);
                        _logger.Information("Selecting back camera");
                    }
                    else
                    {
                        cameraDevice = cameraDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                        _logger.Information("Selecting front camera");
                    }

                    if (cameraDevice == null)
                    {
                        _logger.Error($"Unable to find {cameraType} camera");
                        throw new Exception($"Unable to find {cameraType} camera");
                    }

                    _mediaCapture = new MediaCapture();
                    var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };
                    await _mediaCapture.InitializeAsync(settings);
                    _logger.Information("MediaCapture initialized successfully");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize MediaCapture: {ex.Message}");
                return false;
            }
        }

        public async Task<(string filePath, byte[] imageData, string errorMsg)> TakePhotoAsync(FlashMode flashMode, CameraType cameraType, int imageWidth, int imageHeight)
        {
            try
            {
                _logger.Information($"Taking photo with flash mode: {flashMode}, camera type: {cameraType}, size: {imageWidth}x{imageHeight}");
                if (!await InitializeAsync(cameraType))
                    throw new Exception("Failed to initialize camera");

                var videoDeviceController = _mediaCapture.VideoDeviceController;
                if (videoDeviceController.FlashControl.Supported)
                {
                    _logger.Information($"Flash is supported. Setting flash to: {flashMode}");

                    videoDeviceController.FlashControl.Enabled = flashMode != FlashMode.OFF;
                    videoDeviceController.FlashControl.Auto = flashMode == FlashMode.AUTO;

                    // Verify flash state
                    _logger.Information($"Flash state after setting - Enabled: {videoDeviceController.FlashControl.Enabled}, Auto: {videoDeviceController.FlashControl.Auto}");
                }
                else
                {
                    _logger.Warning("Flash is not supported on this device");
                }

                var imageEncodingProperties = ImageEncodingProperties.CreateJpeg();
                imageEncodingProperties.Width = (uint)imageWidth;
                imageEncodingProperties.Height = (uint)imageHeight;

                // Define the path to save the captured image
                var folder = await StorageFolder.GetFolderFromPathAsync(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                var file = await folder.CreateFileAsync("photo.jpg", CreationCollisionOption.GenerateUniqueName);

                _logger.Information("Capturing photo to storage file");
                await _mediaCapture.CapturePhotoToStorageFileAsync(imageEncodingProperties, file);

                _logger.Information($"Photo captured and saved to: {file.Path}");

                // Read the file into a byte array
                byte[] imageData;
                await using (var fileStream = await file.OpenStreamForReadAsync())
                {
                    imageData = new byte[fileStream.Length];
                    await fileStream.ReadAsync(imageData, 0, (int)fileStream.Length);
                }

                _logger.Information($"Photo size: {imageData.Length} bytes");

                Cleanup();
                return (file.Path, imageData, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to take photo: {ex.Message}");
                return (string.Empty, null, $"Failed to take photo: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            _logger.Information("Cleaning up MediaCapture");
            _mediaCapture?.Dispose();
            _mediaCapture = null;
        }

        public async Task<(string filePath, byte[] imageData, string errorMsg)> CapturePhotoUsingNativeCameraAsync()
        {
            try
            {
                // Launch the native camera app
                var cameraUri = new Uri("microsoft.windows.camera:");
                var success = await Launcher.LaunchUriAsync(cameraUri);
                if (!success)
                {
                    _logger.Error("Failed to launch the camera app.");
                    return (string.Empty, null, "Failed to launch the camera app.");
                }

                // Focus the camera app to ensure it's on top
                var shellUri = new Uri("shell:");
                await Launcher.LaunchUriAsync(shellUri);

                // Get the Pictures library and log the path
                var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
                var picturesFolder = picturesLibrary.SaveFolder;
                var cameraRollFolder = await picturesFolder.CreateFolderAsync("Camera Roll", CreationCollisionOption.OpenIfExists);

                _logger.Information($"Photos will be saved to: {cameraRollFolder.Path}");

                // Use FileSystemWatcher to detect the new photo in real-time
                string newFilePath = null;
                var cancellationTokenSource = new CancellationTokenSource();
                var watcher = new FileSystemWatcher
                {
                    Path = cameraRollFolder.Path,
                    Filter = "*.jpg",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                watcher.Created += (sender, e) =>
                {
                    newFilePath = e.FullPath;
                    _logger.Information($"New photo detected: {newFilePath}");
                    cancellationTokenSource.Cancel(); // Cancel the delay if a new file is detected
                };

                try
                {
                    // Wait for the photo to be captured or timeout after 60 seconds
                    await Task.Delay(60000, cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    // Task was cancelled because a new photo was detected
                }
                finally
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }

                if (newFilePath != null)
                {
                    _logger.Information($"Most recent photo: {newFilePath}");

                    const int maxRetries = 20;
                    const int delayMs = 500;
                    byte[] imageData = null;
                    long lastSize = -1;

                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(newFilePath);
                            if (fileInfo.Length == lastSize)
                            {
                                // File size has stabilized, attempt to read
                                using (var fileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                {
                                    imageData = new byte[fileStream.Length];
                                    await fileStream.ReadAsync(imageData, 0, (int)fileStream.Length);
                                }
                                break;
                            }
                            else
                            {
                                // File size is still changing, wait and check again
                                lastSize = fileInfo.Length;
                                _logger.Information($"Attempt {attempt + 1}: File size is {lastSize} bytes. Waiting for size to stabilize.");
                                await Task.Delay(delayMs);
                            }
                        }
                        catch (IOException ex)
                        {
                            if (attempt == maxRetries - 1)
                            {
                                throw;
                            }
                            _logger.Warning($"Attempt {attempt + 1} to access file failed: {ex.Message}. Retrying in {delayMs}ms.");
                            await Task.Delay(delayMs);
                        }
                    }

                    if (imageData != null)
                    {
                        return (newFilePath, imageData, string.Empty);
                    }
                    else
                    {
                        return (string.Empty, null, "Failed to read the image file after multiple attempts.");
                    }
                }

                _logger.Warning("No new photo detected in the Camera Roll folder.");
                return (string.Empty, null, "No new photo detected in the Camera Roll folder.");
            }
            catch (Exception ex)
            {
                _logger.Error($"An error occurred while capturing the photo: {ex.Message}");
                return (string.Empty, null, $"An error occurred while capturing the photo: {ex.Message}");
            }
        }

    }

    public enum FlashMode
    {
        AUTO,
        ON,
        OFF
    }

    public enum CameraType
    {
        BACK,
        FRONT
    }
}