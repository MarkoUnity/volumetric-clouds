# Volumetric Clouds

A high-quality volumetric cloud rendering system for Unity that provides realistic cloud rendering with customizable parameters.

## Features

- Realistic volumetric cloud rendering
- Customizable cloud parameters
- Height-based cloud generation
- Efficient raymarching implementation
- Example scene included

## Installation

### Using Unity Package Manager

1. Open your Unity project
2. Go to Window > Package Manager
3. Click the + button in the top-left corner
4. Select "Add package from git URL..."
5. Enter the following URL:
   ```
   https://github.com/yourusername/VolumetricClouds.git
   ```
6. Click "Add"

### Manual Installation

1. Download the latest release from the [Releases page](https://github.com/yourusername/VolumetricClouds/releases)
2. Extract the package into your Unity project's `Packages` folder
3. The package will be automatically recognized by Unity

## Usage

1. Import the package into your project
2. Open the example scene in `Example/Scenes/Example.unity`
3. Add the `VolumetricCloudRenderer` component to your camera
4. Configure the cloud parameters using the `VolumetricCloudConfiguration` component

## Requirements

- Unity 2020.3 or later
- Universal Render Pipeline (URP) or Built-in Render Pipeline

## License

This project is licensed under the terms specified in the [LICENSE](License.txt) file.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request. 